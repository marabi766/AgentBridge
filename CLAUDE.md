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

### Remote control

`Remote control` on the dashboard opens a Claude session the operator drives from
another device. Three CLI facts shape it, and each was found the hard way:
`--remote-control` means nothing to a `--print` run (it is silently ignored);
a session whose stdout is redirected is not interactive, so it cannot simply be
run and read; and the link is never printed by the command that starts the
session. The way through is `--bg`, which runs the session in a terminal of its
own and returns a short id, plus `claude logs <id>` to read the link back out of
that terminal — hence `RemoteControlSignal`, which strips the ANSI the TUI paints
before looking for the URL.

`--continue` makes the session a *copy* of the conversation ("started a copy of
that conversation as …"), so it carries the bridge's history without anything
typed there reaching a run the bridge is still watching. That is also why
`OpenClaudeRemoteControlAsync` takes no `_actionLock` and touches no state: it
opens beside the cycle, and needing an idle agent would deny it at the one moment
— mid-run, away from the desk — it is wanted.

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
