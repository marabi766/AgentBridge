# Agent Bridge — working notes

Read [README.md](README.md) first for what this is. This file is what is easy to
get wrong when changing it.

## The shape of the thing

`AgentBridge.Core.Orchestration.AgentOrchestrator` is the whole engine. Every
host — the WPF app, the headless CLI, the tests — drives it through
`IOrchestratorService` and nothing else. It has no UI dependency of any kind and
must keep none: `ProjectReferenceArchitectureTests` fails the build if the
dependency direction slips.

Layering: `Abstractions` ← `Core` ← `Infrastructure` ← hosts. `AgentBridge.Cli`
must never reference `AgentBridge.UIAutomation`; the point of the headless host
is to be a process that cannot touch a window, and a project reference would drag
FlaUI and the UIA3 COM interop into it.

## Concurrency

One `SemaphoreSlim` (`_actionLock`) serialises every state-changing operation:
the two file-change handlers, Pause, Stop, ResetState, and both resend paths.
Only one agent invocation is ever in flight.

The completion probe (`WaitForAgentCompletionAndRecheckAsync`) runs outside that
lock and loops. Anything that resends must return `true` into that loop so the
retry is watched the way the first attempt was — a resend that returns to a
`break` fires once and then nothing observes it, which is how a retry silently
becomes a stall. That bug was written once already; the tests in
`FailedRunRetryTests` exist because of it.

## Deciding an agent has finished

Both waits are passive: the bridge watches two files and has no other signal. So
every guard matters, and each exists because its absence broke a real run.

- **Hash dedup.** A file whose content matches the revision already handled is
  refused. Log this at Information and put it in `LastAction` — it is a dead end,
  not a pause, because the other agent only writes its file in answer to this
  one. It was a debug line once and the bridge sat silent for three restarts.
- **`PredatesInstruction`.** A file last written before the current instruction
  was delivered cannot be the answer to it.
- **`TrailsTheOtherProtocolFile`.** The protocol strictly alternates, so a
  genuine reply is always newer than what it replies to. A report older than the
  current prompt (or a prompt older than the current report) is a leftover from
  an earlier cycle. This one reads the two files' times against each other, so it
  still works after a restart — when nothing in memory says an instruction is
  outstanding and the hash and instruction-time guards are both blind. A real run
  restarted at a checkpoint and consumed a stale report from before a timeout,
  advancing past the point where Continue was possible.
- **Deferral.** A protocol file arriving while the agent process is alive is held
  until it exits, then rechecked via `CheckNowAsync`.
- **Produced nothing.** An agent that was observed working, stopped, and left its
  file unchanged ends the run with an explanation.

## Adapters

`CommandLineAgentAdapter` is the base for both CLI adapters. Members that exist
only to find and focus a window (`ActivateAsync`, `FindConversationAsync`,
`FindInputBoxAsync`) return `true` immediately — that is the point of the
adapter, not an omission.

The instruction goes to **stdin**, never the command line. Instructions are long
and multi-line; passing one as an argument puts its quoting at the mercy of the
shell. Closing the stream is what starts the work.

Both output pumps must be drained. A process whose output nobody reads blocks
when its pipe buffer fills, which looks exactly like an agent that never
finishes.

`BridgeConfiguration.AgentEnvironment` (one `KEY=VALUE` per line) is merged onto
the inherited environment of every agent process, a value there winning. It is
for settings an agent would otherwise rediscover each run — on a machine whose
only route out is a local proxy, `NODE_OPTIONS=--use-env-proxy` (Node's own
`fetch`, and so corepack, then honours the proxy) and
`PNPM_CONFIG_VERIFY_DEPS_BEFORE_RUN=false` (pnpm 11 runs a pre-command check that
fails under a write sandbox). Parsed by `ParseEnvironment`; blank lines, `#`
comments and lines without `=` are skipped rather than failing the run.

### Session reuse, and what a run costs

`ReuseAgentSessions` sends each iteration's instruction into the session the
agent already has instead of a new one. A fresh command line run remembers
nothing, so it re-reads the project and re-explores the repository before it
starts work, and those turns are the expensive part — measured runs spent
sixty-five of them. `ShouldReuseSession` never resumes before this run has
actually delivered to that agent (asking a CLI to continue a conversation that
does not exist fails the run outright, and a fresh project is exactly that case)
and starts clean every `FreshSessionEveryIterations`, so a reused conversation
cannot carry an early dead end into a hundredth iteration.

**Claude only.** `ShouldReuseSession` returns `false` outright for any role but
Claude. Codex's own instruction requires it to independently verify the
repository, diff and test results each iteration rather than trust what it
remembers concluding last time — reusing its session pulls directly against
that. Its CLI adds a narrower, harder reason: `codex exec resume` accepts a
smaller flag set than `codex exec` (no `--sandbox` among them — confirmed
against both commands' own `--help`, not guessed), so an ordinary iteration
resumed this way could fail outright on a configuration `exec` itself accepts
without complaint. `CodexCliAdapter.ExecOnlyFlags` strips exactly those flags
before `resume --last` is assembled, but that only protects Continue and
Recover, which resume Codex deliberately; automatic reuse between ordinary
iterations stays off.

`ClaudeCliArguments` defaults to include `--autocompact auto` (confirmed against
`claude --help`, not guessed), so once a resumed session grows large enough
Claude summarises its own history instead of every later turn re-reading all of
it at full price. It is a fallback, not a substitute for the two limits above —
a summary is still real context on every subsequent turn. **Codex has no
equivalent and none was added.** Nothing in `codex --help`, `codex exec --help`
or `codex exec resume --help` compacts, summarises or manages context; its only
history-related subcommands (`archive`, `delete`, `migrate-rollouts`) manage
saved session files on disk, not what one session carries forward. Automatic
reuse being permanently off for Codex already keeps this from mattering for
ordinary iterations.

`ClearClaudeSessionAsync` is the bridge's own `/clear`: resets
`_claudeSessionStarted` so Claude's next *ordinary* delivery starts fresh
rather than resuming. **Claude only, and not by oversight.** A `ClearCodexSessionAsync`
was shipped once alongside it. Automatic reuse is already permanently off for
Codex, so there was never a flag for it to reset — it updated `_lastAction` and
nothing else, reporting an action that had not actually happened. It was
removed rather than kept as a button that does nothing. What neither version
could ever promise, and what any future attempt at a Codex one needs to solve
first: Continue and Recover resume whichever session each CLI itself considers
most recent, a choice this class has no way to affect, so a Continue or Recover
clicked before the next ordinary delivery goes out still lands in the very
session Clear just asked to be forgotten.

**Each `Invoke*Async` must call `ShouldReuseSession` with its own role.**
`InvokeCodexAsync` calling it with `AgentRole.Claude`, or the reverse, type
checks and runs — nothing throws, nothing logs a warning — and produces exactly
the two failures above: Claude never resumes because it is being asked about
Codex's state, and Codex resumes whenever Claude's state happens to say so.
This shipped once. `SessionReuseTests` pins each invocation to its own role by
running two full iterations and checking which adapter actually saw
`ContinueLastSessionAsync` — a test that only checks the argument passed to
`ShouldReuseSession` would have kept passing regardless of which literal was
there, since both are the same type.

**Anything that runs after `StopRuntimeResources()` must not depend on the
token that call just cancelled.** `StartCompletionWatchdog` starts the
completion probe on `_runCts.Token`, and `StopRuntimeResources()` cancels and
disposes `_runCts` — so a give-up branch reached from inside that probe (the
allowance-exhaustion limit, an agent that produced nothing) that calls
`StopRuntimeResources()` and then `PersistStateAsync(cancellationToken)` with
the *same* `cancellationToken` parameter is persisting with a token already
cancelled. Nothing crashes: `JsonStateStore.SaveAsync` awaits its file lock
with that token first and throws before writing anything, and the outer loop's
own `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`
— written for the ordinary Stop/Pause lifecycle — swallows it as exactly that.
`DesktopNotificationService.NotifyAsync` does the same for the same reason: it
checks `cancellationToken.IsCancellationRequested` and quietly returns. A real
run sat for hours believing its previous iteration was still in progress: the
log showed the Error transition, the state file did not, and no notification
of any kind went out. Both give-up branches now persist and notify on
`CancellationToken.None` — this is the run's last word and it has to land
regardless of what ended the run. `InMemoryStateStore.SaveAsync` was changed to
throw on an already-cancelled token, matching `JsonStateStore`, specifically so
a regression here fails `AgentOrchestratorFailurePersistenceTests` instead of
only a real overnight run.

`AgentRunCostReader` reads what a run reported spending off the line that ends
it, and one readable summary is logged outside the line budget — it is the answer
to "why is my allowance going", and it arrives exactly where the budget has
already run out. `"input_tokens"` is also the tail of
`"cache_read_input_tokens"`; the pattern is anchored so cached context is not
reported as freshly paid for, which would invert the number entirely.

### Continue, Retry, and the completion probe

`Continue Claude` / `Continue Codex` send one word (`"continue"`) into the agent's
*existing* session — `--continue` for Claude, `resume --last` after the verb for
Codex (`CommandLineAgentAdapter.ResumeArguments`). A command line run has no
memory of the last one, so the word only means something if the session comes
back with it.

**Do not gate them on the iteration counter the way Retry is gated.** Retry
resends the instruction the bridge is holding and has none at iteration zero;
Continue speaks to a session that exists on disk regardless of what the bridge
has counted. Copying Retry's guard onto the Continue buttons disabled them for a
run that had been reset and restarted at a checkpoint — exactly the case they
were added for. The orchestrator never had that guard; only the desktop did.

`Recover Claude` / `Recover Codex` take the same route through `ResumeAgentAsync`
and differ only in the message: a rendered `*RecoveryTemplate` instead of the one
word. The distinction is what the agent may trust. A session that merely paused
still knows what it did; one killed mid-edit can believe it finished work that
never reached disk, so the recovery message says the run was cut short and sends
it to `git status` and the diff for the facts before it carries on. Both keep the
iteration where it was — the same task is being finished, not a new one started.

Any operator-driven delivery — Continue, Recover or either Retry — calls `TakeOverDelivery`,
which bumps a per-role `_*DeliveryEpoch` and clears the adapter's announced
allowance wait (`IWaitsOutQuotaLimits.ForgetAnnouncedQuotaWait`). The running
`WaitForAgentCompletionAndRecheckAsync` probe captures the epoch and, when it
moves, re-observes the new delivery from the top instead of resending. The
resend/fail helpers re-check the epoch after taking `_actionLock`. Without all of
this, a manual Continue during an allowance wait is followed minutes later by an
automatic full resend from the probe that was still counting down to the old
reset — the operator switched accounts precisely so that wait no longer applies.

### Remote control — removed, do not re-add without solving this first

A "Remote control" dashboard button once opened a Claude session the operator
could drive from another device, via `claude --bg --continue --remote-control`.
It was removed on 2026-09-11: every click left an idle background Claude Code
session running (`claude --bg` returns immediately and does not stop on its
own), and nothing in the bridge or the button ever stopped one. Eight
accumulated across one working session, found via `claude agents --json` while
investigating unrelated system slowness they were plausibly contributing to.

The session it opened was also always a *fork*, never the bridge's own live
one — `claude --continue` says as much on the way in ("started a copy of that
conversation as …") — so it would show whatever the conversation looked like
at the moment of the click and then go stale; the page not updating with the
bridge's later progress was a legitimate operator complaint, not something a
fix on the CLI side would have solved by itself.

Re-adding this needs both problems solved together: the opened session has to
be stopped by something (a background sweep, a hard cap on how many stay
open, or the button itself closing the previous one before opening a new one),
and a forked-but-static session has to be presented as what it is rather than
implied to be live. `--bg` starting the session in a terminal of its own and
returning a short id, and `claude logs <id>` being the only way to read back
the link it draws inside that terminal rather than printing, are still true of
the CLI and still the mechanism to build on if this comes back.

### Reading what an agent printed

`AgentQuotaSignal` and `AgentStreamLine` both parse agent output. **Every pattern
in them is taken from output that was actually observed, and the tests quote real
lines verbatim.** The first version of `AgentQuotaSignal` guessed at the wordings,
matched none of the three signals Claude Code emits, and turned a two-hour wait
into a dead run. If you add a pattern, add the line you saw it in.

`AgentStreamLine` renders before anything is logged. Anything that is not the
expected JSON shape passes through unchanged — Codex prints plain prose, and a
line cut short by the length cap no longer parses. Swallowing either would lose
half the loop's output and all of its longest lines.

Rendering happens **before** the line budget is spent, so the configured limit
buys readable lines rather than token counters.

## Time, and tests

`AgentOrchestrator` takes a `TimeProvider`; use it rather than `DateTime.Now`.

Do not pin a real timestamp in a test. One test hard-coded the epoch from a live
quota refusal: it passed until that moment arrived and failed every run
afterwards. Build times relative to `UtcNow`.

## Settings

`BridgeConfiguration` is a record with defaults; missing JSON properties take the
record's default, so adding a property is safe for existing installs. The GUI
loads it into `MainWindowViewModel` and writes it back whole — a new setting
needs the field, the property, the line in `BuildConfiguration`, the line in
`LoadSettings`, and the control in `MainWindow.xaml`. Miss one and it silently
resets on the next save.

`DefaultAgentAdapterProvider` resolves the desktop-versus-CLI choice **per call**,
not at startup, so changing it in Settings takes effect without a restart.

## The WPF layer has no tests

No test project references `AgentBridge.App`. Command availability
(`CanRetryClaude`, `CanResetState`, …) and XAML bindings are verified only by
running the application. Three shipped bugs came from this: Reset was gated on
`HasError` and unreachable in the state that needed it; the tray showed
`SystemIcons.Application` instead of the app's own icon; and the Continue buttons
sat permanently disabled because `RaiseStatusProperties()` raised the
`CanContinue*` property-change notifications but not the matching
`Continue*Command.RaiseCanExecuteChanged()` — `AsyncCommand` does not hook
`CommandManager.RequerySuggested`, so a new command needs its `.RaiseCanExecuteChanged()`
line added there by hand or it never re-evaluates. Check button states on screen
before claiming a UI change works.

## Icons

`Assets\AgentBridge.ico` carries ten sizes, 16–256: BMP with an AND mask up to
48, PNG above. Each size is produced by halving repeatedly rather than one
bicubic pass, which is what keeps 16px legible. Installer shortcuts name the icon
explicitly — an inferred icon is cached per path, so an upgrade reusing the path
keeps showing the old one.

## Logs

`%LOCALAPPDATA%\AgentBridge\logs` for the app, `...\AgentBridge\cli\logs` for the
headless host. One file per **UTC** day, which is not today's local date. The
writer holds the current file open with `FileShare.Read`, so readers must open
with `FileShare.ReadWrite` and the file cannot be deleted while running.

`FileLogService.ReadSinceAsync` reads forward from a byte offset. Stop at the
last newline: a line the writer is mid-way through must be left for next time, or
it is shown truncated and never corrected, because the position has moved past
it.

## Notifications

`INotificationService` is the seam. Every host wraps its channels in a
`CompositeNotificationService`, which isolates failures — the tray balloon still
appears when Telegram is down, and vice versa. Both the WPF app and the headless
host register Telegram; the headless host has no other channel.

`TelegramNotificationService` reads its token and chat id from settings on every
call, not once, so a correction takes effect without a restart. It stays silent
until `TelegramNotificationsEnabled` is on and both identifiers are set. It never
throws — a chat that cannot be reached must not stop a run. Its `HttpClient` is
the default one, which reads `HTTPS_PROXY`; this machine has no other route out.

`NotifyAsync` carries an optional `NotificationAttachment` (content, not a path —
no file I/O in a notifier, no race with the writer). The orchestrator attaches
the protocol file at the two points an agent delivers one
(`NotifyAgentFinishedAsync`). A channel that cannot send a file ignores it.

Every outbound Telegram message — a notification, a command's reply — is run
through `TelegramMessageFormatting.Prefixed`, which puts `[ProjectFolderName]`
in front. One operator can run the bridge against more than one project through
the same bot; a message with no project name is ambiguous the moment that
happens.

### Telegram commands

`TelegramCommandListener` is a `BackgroundService` that polls `getUpdates` and
drives `IOrchestratorService` from `/run`, `/stop`, `/pause`, `/resume`,
`/status`, `/retry_claude`, `/retry_codex`, `/continue_claude`, `/continue_codex`,
`/recover_claude`, `/recover_codex` and `/help`. `TelegramCommandParser` is
deliberately the only thing that knows what a message's text means — pure,
tested directly, no HTTP or orchestrator anywhere near it, the same shape as
`AgentQuotaSignal`.

**Never map bare `/start` to starting the bridge.** Telegram's own client sends
a literal `/start` the first time anyone opens a chat with a bot, before they
have read anything it says — onboarding chrome, not a considered instruction.
Starting the bridge is `/start`'s natural-sounding name and is deliberately
`/run` instead; bare `/start` is handled as `/help`.

Two separate guards gate every command, and both matter:
`BridgeConfiguration.TelegramCommandsEnabled` (off even once notifications are
on — receiving a message is harmless, acting on one is a materially bigger
thing to turn on) and the sender's chat id matching `TelegramChatId` exactly.
The second one is not optional even with the first off by default: a bot token
that leaks must not become an open control channel for anyone who finds it, so
a message from any other chat is ignored without a reply — not even an error,
which would confirm to a stranger that commands exist to try.

`ExecuteAsync` must never let an exception escape it. `BackgroundService`'s
default failure behaviour under `Host.CreateApplicationBuilder` — used by both
hosts — is `BackgroundServiceExceptionBehavior.StopHost`: an unhandled fault in
a hosted service's `ExecuteAsync` stops the *entire application*, not just that
service. A Telegram hiccup taking down the whole bridge would be far worse than
the outage it came from, so the whole poll body is one try/catch that logs and
backs off rather than throws.

The headless host has no generic `IHost` driving hosted-service lifecycles —
`Program.cs` builds a bare `ServiceCollection` and runs one session start to
finish — so `TelegramCommandListener` is resolved and its `StartAsync`/`StopAsync`
called by hand, bracketing the run, rather than registered as a true hosted
service there.

## Scripts

`scripts\watch-bridge.ps1` must run under **Windows PowerShell 5.1** — that is
what `powershell` launches on a stock machine. No `??`, no `?.`, no ternary. It
was shipped with `??` once and failed to parse before printing a line.

## Conventions

- Comments explain **why**, especially where the obvious approach was wrong.
- Failure paths return `false` or a status; only genuinely unexpected faults
  throw. An adapter must never throw for "the app is not running".
- Never report success that was not observed. `SupportsRealMessageDelivery`
  gates a live run; a `LogClearResult`-style report should name what it could not
  do rather than claim it did.
