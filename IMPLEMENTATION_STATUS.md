# Agent Bridge — Backend Implementation Status

> Note on filenames: `CodexPrompt.md`, `PROJECT_REQUIREMENTS.md`, and
> `PROJECT_STATUS.md` already exist in this repository, produced by a separate
> Codex session tracking its own milestone plan. This document intentionally
> uses a different name and does not modify those files — it tracks the
> backend build done in this chat session specifically.

Date: 2026-09-03. Original scope: backend-first phase (state machine, orchestrator,
file watcher, persistence, Git integration, retry/timeout, fake agent
adapters, tests). **Update:** a first functional WPF shell now implements the
Dashboard, Activity, Diagnostics, and Settings surfaces. Real UI Automation
message delivery is now implemented behind explicit Live-mode configuration and confirmation. The Dry Run desktop experience also
includes Setup, guarded recovery, tray/notifications, single-instance behavior,
and light/dark themes — see `FUTURE_UI.md`, `UI_DESIGN_REVIEW.md`,
and `UI_AUTOMATION.md`.

## Requirement status

| Requirement | Status | Evidence |
|---|---|---|
| Windows GUI application exists | Done (first increment) | `AgentBridge.App` is a WPF WinExe with a functional light-theme shell |
| Project folder can be selected + validated | Done | `IProjectService.ValidateProjectPathAsync`; `ProjectServiceTests` |
| Git repository can be validated (read-only) | Done | `GitService`; `GitServiceTests` (init/commit/modify scenarios) |
| ClaudeResultReport.md / CodexPrompt.md monitored | Done | `FileWatcherService`; `FileWatcherServiceTests` |
| File stability detection | Done | N-consecutive-identical-hash polling; locked-file retry test passes |
| Debounce | Done | `RapidSuccessiveSaves_ProduceExactlyOneEvent_ForTheFinalContent` |
| Duplicate event suppression | Done, two layers | Watcher-level hash dedup + orchestrator-level persisted-hash dedup; both covered in unit and integration tests |
| Hash tracking (SHA-256) | Done | `FileWatcherService` computes and reports SHA-256; snapshot persists last processed hash per file |
| Persistent state (atomic, corruption-safe) | Done | `JsonStateStore`; temp-file + `File.Replace`; corrupted files backed up and reported distinctly |
| State machine | Done | `BridgeStateMachine`; explicit transition table; `BridgeStateMachineTests` |
| Orchestrator | Done | `AgentOrchestrator`; UI-technology-agnostic |
| Iteration tracking | Done | Incremented only on a genuinely new, in-limit Claude report |
| Maximum iterations | Done | Enforced before incrementing; `MaximumIterations_StopsAutomationInsteadOfExceedingLimit` (unit + integration) |
| Pause / Resume / Stop | Done | Including the Resume-picks-up-a-change-that-arrived-while-paused case, which surfaced and fixed a real bug (see `ARCHITECTURE.md`) |
| Dry Run | Done | `InvokeAgentAsync` short-circuits before any adapter call when `DryRun=true`; state still advances so the whole pipeline is testable |
| Logging | Done | `DailyFileLoggerProvider` (one file per UTC day), `FileLogService` for read-back; message-length safety cap |
| GUI dashboard | Done (first increment) | Status, controls, cycle, Git, agents, protocol timestamps, and persistent Dry Run banner |
| Settings (persistence + validation) | Done (first increment) | Atomic backend validation plus a functional WPF Settings surface |
| System tray | Done | Start/pause/stop/open/exit controls and minimize-to-tray behavior |
| Notifications | Done (desktop balloon) | Runtime status/error notifications with persisted opt-out |
| Single-instance behavior | Done | Mutex plus existing-instance activation |
| Fake agents | Done | `FakeClaudeAdapter` / `FakeCodexAdapter`, fully configurable behavior |
| Unit tests | Done | 38 tests in `AgentBridge.Core.Tests` |
| Infrastructure tests | Done | 32 tests in `AgentBridge.Infrastructure.Tests` (real files, real git.exe) |
| End-to-end simulation | Done | 8 tests in `AgentBridge.Integration.Tests`, real infra + fake adapters, multi-iteration, real disk writes |
| Claude Desktop adapter | Implemented, canary pending | Exact active-title targeting, unique input selection, single Invoke, positive receipt verification |
| ChatGPT Desktop/Codex adapter | Implemented, canary pending | Same fail-closed contract; read-only discovery verified against installed app |
| UI Automation diagnostics | Done (basic) | `GetDiagnosticsAsync` dumps a live 3-level automation tree against the real installed apps |
| Real application detection | Done | Verified against the actually-installed Claude Desktop and ChatGPT Desktop (MSIX packages) on this machine |
| Conversation detection | Done | Exact configured active-title marker; ambiguous/wrong targets fail closed |
| Message sending (verified delivery) | Implemented, real canary pending | Requires cleared input plus a new exact rendered message after one Send invocation |
| Error handling | Done | Every failure path (timeout, send failure, corrupted state, ambiguous restart state, agent unreachable) transitions to `Error` with a descriptive message, never crashes, never hangs |
| Retry and timeout | Done | `ExponentialBackoffRetryPolicy` (bounded, testable via `FakeTimeProvider`); per-invocation timeout via linked `CancellationTokenSource` |
| README / ARCHITECTURE / TROUBLESHOOTING / CONFIGURATION docs | Partial | `ARCHITECTURE.md`, `UI_AUTOMATION.md`, `FUTURE_UI.md` written this phase; `README.md`/`TROUBLESHOOTING.md`/`CONFIGURATION.md` not yet written |
| Publishing process | Not started | Release-stage work, out of scope for this phase |
| Release build succeeds | Verified | Debug and Release solution builds both pass with 0 errors and 0 warnings |

## Test summary

```
AgentBridge.Core.Tests:          48 passed, 0 failed
AgentBridge.Infrastructure.Tests: 43 passed, 0 failed
AgentBridge.Integration.Tests:     8 passed, 0 failed
AgentBridge.UIAutomation.Tests:  16 passed, 0 failed
Total:                           117 passed, 0 failed
```

Run with: `dotnet test AgentBridge.slnx`

## Real bugs found and fixed during this phase (not merely "implemented")

1. **Transition log always showed "X -> X".** `Transition()` logged
   `_stateMachine.Current` as the "from" state *after* the mutation had
   already happened. Fixed by capturing `from` before calling
   `TryTransition`. Would have made the future log viewer useless for
   diagnosing state history.
2. **`AgentOrchestrator.Dispose()` crashed on shutdown.** The DI container
   tracks the same singleton for disposal twice (once under its concrete
   registration, once under the `IOrchestratorService` factory registration
   that returns the same instance) — a real, reproducible .NET DI pitfall.
   `Dispose()` was not idempotent, so the second call threw
   `ObjectDisposedException` on a cancellation token source, crashing app
   shutdown. Fixed with an `Interlocked`-guarded idempotency flag.
3. **`LastError` stayed stuck after a successful restart.** A prior failed
   `Start` left `LastError` set; a subsequent successful `Start` never
   cleared it, so the dashboard would show a stale error alongside "Waiting
   for Claude" as if something were currently wrong. Fixed by clearing
   `LastError` at the point a `Start` actually succeeds validation.
4. **Pause silently dropped changes.** (Found via the integration test
   suite, not the unit tests — this is why the "full simulation" tests exist.)
   A file changed while the bridge was `Paused` was never actually reprocessed
   on `Resume`, because the file watcher's own duplicate-hash suppression
   didn't distinguish "I told someone about this" from "someone actually
   acted on it." Fixed by making `CheckNowAsync` bypass the watcher's own
   emission history and rely on the orchestrator's persisted hash as the
   sole authority on what's genuinely new. Full reasoning in `ARCHITECTURE.md`.

All four were caught and fixed within this session, before being reported as
done — nothing above is a known-and-ignored defect.

## Known limitations (see UI_AUTOMATION.md and FUTURE_UI.md for detail)

- Real message delivery has not yet received a user-approved one-message
  canary against the installed applications. Read-only target/input discovery
  has been verified for both apps.
- No installer or start-on-login registration; packaged modern toasts remain a release enhancement.
- The WPF shell does not yet include the optional rich recovery timeline or advanced Activity filtering/export.

## Recommended next phase

With explicit user approval, perform one controlled real-message canary per
application, then exercise one bounded two-agent loop before packaging.
