# Agent Bridge — Architecture

## What this is

Agent Bridge is a Windows desktop orchestrator that eliminates the manual
copy/paste loop between Claude Code (inside Claude Desktop) and Codex (inside
ChatGPT Desktop). Two Markdown files in the target project repository —
`ClaudeResultReport.md` and `CodexPrompt.md` — are the communication protocol.
The bridge watches them, and when one changes and stabilizes, it activates the
other application and delivers the next instruction.

## Backend-first, UI deferred

This phase of the project deliberately builds the **entire backend** —
state machine, orchestrator, file watcher, persistence, Git integration,
retry/timeout/cancellation, fake agent adapters — and defers the real
dashboard/tray/settings/wizard UI to a later phase. `AgentBridge.App` is
currently a minimal console host, not the product.

This is not a shortcut. It is the point: the backend is fully exercised by
`AgentBridge.Integration.Tests` — real file watcher, real JSON persistence,
real Git service — end to end, without any WPF/WinUI code and without Claude
Desktop or ChatGPT Desktop needing to be open. The eventual UI is a thin
consumer of `IOrchestratorService`, `ISettingsService`, `ILogService`,
`IProjectService`, and `IAgentDiagnosticsService` — nothing else. Swapping
`AgentBridge.App` from a console host to WPF later touches only that project.

## Solution layout

```
/src
  AgentBridge.Abstractions   — models + interfaces only. No implementation, no I/O.
  AgentBridge.Core           — state machine, orchestrator, retry, templates. Pure logic.
  AgentBridge.Infrastructure — file watcher, JSON persistence, Git (shells to git.exe),
                                logging, notifications, project/settings services.
  AgentBridge.UIAutomation   — Windows UI Automation adapters (FlaUI). Isolated; the
                                only project allowed to know Claude Desktop / ChatGPT
                                Desktop exist as Windows applications.
  AgentBridge.Fakes          — FakeClaudeAdapter / FakeCodexAdapter. Let the full
                                orchestration loop run without any real GUI app.
  AgentBridge.App            — composition root. Currently a console host; a future
                                phase adds a WPF project here (or replaces this one).
/tests
  AgentBridge.Core.Tests          — orchestrator/state-machine logic against fully
                                     doubled infrastructure. Fast, deterministic.
  AgentBridge.Infrastructure.Tests — real file I/O, real git.exe, real JSON persistence.
  AgentBridge.Integration.Tests    — real infrastructure + fake adapters, driven through
                                      actual file writes on disk. The "full simulation."
```

## Dependency direction (enforced by project references, not convention)

```
Abstractions  <-- Core  <-- Infrastructure  <-- App
Abstractions  <-- UIAutomation             <-- App (wiring deferred to the UI phase)
Abstractions  <-- Fakes                    <-- App
```

- `Abstractions` depends on nothing but `Microsoft.Extensions.Logging.Abstractions`.
- `Core` depends only on `Abstractions`. It has never seen `System.IO`,
  `FileSystemWatcher`, `Process`, or any Windows API.
- `Infrastructure` implements the interfaces `Core` and `Abstractions` define.
  It knows about files, JSON, and shelling out to `git.exe` — never about
  Claude Desktop or ChatGPT Desktop.
- `UIAutomation` is the only project that references FlaUI / UI Automation and
  knows Claude Desktop / ChatGPT Desktop process names and window structure.
  It implements `IAgentAdapter` and nothing else is allowed to depend on it
  for orchestration logic.
- `App` is the only project allowed to reference everything — it is the
  composition root that wires interfaces to implementations via DI.

`AgentBridge.App` currently targets `net10.0` and only references `Fakes` for
agent adapters (see "Why UIAutomation isn't wired into App yet" below).
`AgentBridge.UIAutomation` targets `net10.0-windows` (required for FlaUI).

## The state machine

`AgentBridge.Core.StateMachine.BridgeStateMachine` is the single source of
truth for "what state are we in" — there are no scattered booleans anywhere
else. States:

```
Idle -> WaitingForClaudeReport -> ClaudeReportDetected -> WaitingForCodex
     -> CodexProcessing -> WaitingForCodexPrompt -> CodexPromptDetected
     -> WaitingForClaude -> ClaudeProcessing -> (loop) WaitingForClaudeReport
```

Every active state can transition to `Paused` (remembering `StateBeforePause`
for `Resume`) or `Stopped`. Illegal edges (e.g. `WaitingForClaudeReport ->
ClaudeProcessing`, skipping the whole middle of the cycle) are rejected by
`TryTransition`, never silently allowed. See
[`BridgeStateMachine.cs`](src/AgentBridge.Core/StateMachine/BridgeStateMachine.cs)
for the full transition table.

## The orchestrator

`AgentBridge.Core.Orchestration.AgentOrchestrator` implements
`IOrchestratorService` (Start/Pause/Resume/Stop/TestConnection/GetStatus) and
is the only place orchestration logic lives. It:

- Serializes every state-changing operation through one `SemaphoreSlim` — a
  file-change handler, `Pause`, `Stop`, and `ResetState` can never race.
- Applies **two independent layers of duplicate suppression**: the file
  watcher itself dedupes by content hash within its own emission history, and
  the orchestrator separately compares against its own persisted
  `LastClaudeReportHash` / `LastCodexPromptHash` before ever acting.
- Enforces `MaximumIterations` before incrementing the iteration counter —
  never after acting.
- Wraps every agent invocation in a per-operation timeout
  (`AgentTimeoutSeconds`) and a bounded exponential-backoff retry
  (`RetryCount`/`RetryInitialDelayMilliseconds`/`RetryMaxDelayMilliseconds`)
  via `IRetryPolicy`. `SendMessageAsync` itself is never retried automatically
  — a failed send is a terminal failure for that invocation (state -> `Error`),
  because retrying a send risks a duplicate prompt.
- Refuses to auto-resume from an ambiguous persisted state (anything other
  than `Idle`/`Stopped`/`Paused`/a `Waiting*` state/`Error`) on restart —
  see "Safe recovery" below.

## File watching

`AgentBridge.Infrastructure.FileWatching.FileWatcherService` watches one file.
Pipeline: raw `FileSystemWatcher` event -> debounce (coalesce a burst of
saves) -> poll until N consecutive reads produce the identical SHA-256 hash
(stability) -> suppress if identical to the last hash *this watcher already
emitted* -> raise `StableChangeDetected`.

A new raw event mid-cycle cancels the in-flight debounce/stability wait and
restarts it — this is what makes "saved 5 times in 300ms" produce exactly one
event for the final content, without any fixed sleep.

**`CheckNowAsync` deliberately bypasses that same-watcher dedup.** It exists
for catch-up scenarios: on `Start` (pick up a change that happened while the
bridge wasn't running) and on `Resume` (pick up a change that arrived while
paused). A real bug was found and fixed during integration testing: if
`CheckNowAsync` also honored the watcher's "already emitted this hash"
memory, a file changed while paused would be silently lost forever, because
the watcher's own background cycle had already "emitted" it once — even
though the orchestrator ignored that emission because it was paused. The fix:
the watcher's job is to truthfully report current stable content;
*consumption* tracking belongs entirely to the orchestrator's own persisted
hash, which is authoritative. See the doc comments on `IFileWatcher.CheckNowAsync`
and `FileWatcherService.RunCycleAsync` for the full reasoning.

## Persistence

`AgentBridge.Infrastructure.Persistence.JsonStateStore` and
`JsonConfigurationService` both write via a temp file in the same directory
followed by `File.Replace`/`File.Move` — a crash mid-write can never leave a
half-written file. A file that fails to parse is backed up with a timestamped
`.corrupted-*.bak` suffix and reported distinctly (`StateLoadStatus.Corrupted`)
rather than silently discarded or silently accepted. The orchestrator treats
`Corrupted` as `Error` and refuses to guess — see "Safe recovery."

## Safe recovery

On `Start`, a persisted `BridgeStateSnapshot` is only auto-resumed if its
`CurrentState` is one where no agent action could plausibly have been
in-flight when the process last stopped: `Idle`, `Stopped`, `Paused`, any
`Waiting*` state, or `Error` itself. A recovered `CodexProcessing`,
`ClaudeProcessing`, `ClaudeReportDetected`, or `CodexPromptDetected` state
means an agent action may have been interrupted mid-flight — the orchestrator
forces itself into `Error` with a descriptive `LastError` and refuses to
proceed until `ResetStateAsync` is called explicitly. This is covered by
`AgentOrchestratorTests.AmbiguousMidActionStateOnRestart_NeverBlindlyResumes`
and `FullOrchestrationCycleTests.PersistedAmbiguousMidActionState_*`.

## Git integration

`AgentBridge.Infrastructure.Git.GitService` shells out to `git.exe` with
`ProcessStartInfo.ArgumentList` (never a concatenated shell string, so a path
or branch name cannot cause command injection) and only ever runs read-only
commands: `rev-parse --show-toplevel`, `branch --show-current`, `status
--porcelain=v1`, `log -1`. It never commits, pushes, pulls, resets, or checks
out anything — the bridge is an observer.

## Agent abstraction

`IAgentAdapter` (in `Abstractions`) is the uniform contract:
`IsApplicationRunningAsync`, `LaunchApplicationAsync`, `IsReadyAsync`,
`ActivateAsync`, `FindConversationAsync`, `FindInputBoxAsync`,
`SendMessageAsync`, `GetStatusAsync`, `GetDiagnosticsAsync`. Implementations
must never throw for an expected negative outcome (app not running, element
not found) — they return `false`/`Unknown` and let the orchestrator's retry
and error-state policy decide what happens next.

Two implementations exist:

- **`AgentBridge.Fakes.FakeClaudeAdapter` / `FakeCodexAdapter`** — fully
  configurable via `FakeAgentAdapterState` (simulate not-running, launch
  failure, not-ready, activation/conversation/input-box failure, send
  failure, arbitrary send latency for timeout testing). These drive every
  orchestration test in this phase, including the full multi-iteration
  simulation in `AgentBridge.Integration.Tests`.
- **`AgentBridge.UIAutomation.Adapters.ClaudeDesktopAdapter` /
  `ChatGptDesktopAdapter`** — see `UI_AUTOMATION.md` for exactly what is and
  isn't implemented yet, and why.

## Why UIAutomation isn't wired into App yet

`AgentBridge.App` currently targets `net10.0`, not `net10.0-windows`, and
only registers the two `Fakes` adapters. This is intentional for this phase
(backend-first; see `FUTURE_UI.md` and the phase instructions this was built
under) — real UI Automation message delivery (`FindConversationAsync`,
`FindInputBoxAsync`, `SendMessageAsync`) is not implemented yet (see
`UI_AUTOMATION.md`), so wiring the real adapters into the App host would only
mean every real run immediately hits `Error`. When that phase begins, `App`
switches its TFM to `net10.0-windows`, adds a project reference to
`UIAutomation`, and its DI registration swaps `FakeClaudeAdapter`/
`FakeCodexAdapter` for `ClaudeDesktopAdapter`/`ChatGptDesktopAdapter` — no
other project changes.

## Configuration vs. state

`BridgeConfiguration` (user-editable setup: paths, templates, timeouts,
`MaximumIterations`, `DryRun`, ...) and `BridgeStateSnapshot` (runtime
progress: current state, iteration, hashes, timestamps) are deliberately
separate types persisted to separate files
(`%LOCALAPPDATA%\AgentBridge\settings.json` and `AgentBridgeState.json`;
see `AppPaths`). Both are outside the Agent Bridge source repo *and* outside
whatever target project repo the bridge orchestrates, by design — the bridge
never writes its own internal files into a user's git repo.

## Concurrency and loop protection

Before any action: the semaphore serializes it, the state machine's guard
(`if (_stateMachine.Current != BridgeState.WaitingForClaudeReport) return;`)
rejects it if the bridge isn't expecting that file right now, the hash
comparison rejects it if the content isn't genuinely new, and
`MaximumIterations` is checked before the iteration counter increments. All
four checks are independent and any one of them stops processing —
`AgentOrchestratorTests.ConcurrentIdenticalFileEvents_OnlyProcessOnce` and the
integration-test equivalent verify this directly by firing five identical
events and rapid duplicate real file saves.
