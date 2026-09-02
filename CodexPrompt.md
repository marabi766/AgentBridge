# Codex Prompt

## Iteration

2

## Decision

NEEDS_FIXES

## Objective

Harden the existing backend against false message-delivery success and lifecycle/recovery stalls. Correct only the four verified defects below and add regression tests. Do not implement real UI Automation or any UI feature in this iteration.

## Current Situation

- The solution restores and builds in Debug with 0 warnings/errors.
- Codex independently reproduced all 78 passing tests: 38 core, 32 infrastructure, and 8 integration.
- The component boundaries are broadly acceptable, but passing tests omit critical negative paths.
- `ClaudeResultReport.md` does not exist. The implementation summary supplied outside the repository is not a substitute for the protocol file.

## Problem

1. `AgentBridge.App` registers `FakeClaudeAdapter` and `FakeCodexAdapter`. When persisted configuration sets `DryRun=false`, fake adapters return success and the orchestrator records a real “Sent instruction” even though no desktop application received a message.
2. `ApplyRecoveredState` treats `WaitingForCodex` and `WaitingForClaude` as safe, but `StartAsync` does not execute either pending action. A restart in those states stalls permanently.
3. `StopForMaxIterationsAsync` changes state to `Stopped` but does not cancel `_runCts` or stop the two watchers. `StopAsync` then returns early because the state is already `Stopped`, so cleanup can never occur through that path.
4. `ExponentialBackoffRetryPolicy.ExecuteAsync<T>` retries thrown exceptions only. Adapter operations that signal transient not-ready/not-found with `false` return immediately, so `InvokeAgentAsync` does not perform the documented bounded readiness/discovery retries.

## Required Changes

1. Add an explicit adapter capability/simulation signal to the abstraction (choose a precise name such as `SupportsRealMessageDelivery` or an immutable capabilities value). Do not detect fake adapters using concrete-type checks or namespace/name strings.
2. Before starting a non-dry run, verify both selected adapters support real message delivery. If either does not, fail closed into `Error` with a clear message, start no watchers, invoke no adapter methods, and never record a “Sent instruction” action. Dry-run operation with fakes must remain supported.
3. Mark the current UI Automation adapters as not supporting real delivery until their conversation/input/send implementations actually exist. Mark fake adapters as simulation-only.
4. Make restart handling deterministic for persisted `WaitingForCodex` and `WaitingForClaude`. For this iteration, prefer the safest small change: classify them as recovery-required/ambiguous, transition to `Error`, explain the pending action in `LastError`, and invoke no adapter. Do not add automatic resend behavior without an exact-once delivery design.
5. Centralize stop-resource cleanup so every path that reaches `Stopped` cancels the active run and stops both watchers. Calling `StopAsync` while already `Stopped` must still be safe and must guarantee cleanup. Do not dispose watchers on ordinary stop if restart is expected; preserve existing Start-after-Stop behavior.
6. Add an explicit retry operation for boolean conditions (for example `ExecuteUntilTrueAsync`) that retries `false` and eligible transient exceptions with the existing bounded backoff/cancellation policy. Use it for running/readiness/activation/conversation/input discovery as appropriate. Do not retry `SendMessageAsync`; duplicate delivery remains unacceptable.
7. After auto-launch reports success, verify application-running/readiness through the bounded condition retry instead of immediately assuming the launched app is ready.
8. Keep exception retry semantics backward compatible and keep all retry counts/delays bounded and cancellation-aware.
9. Create or replace `ClaudeResultReport.md` at the end with truthful repository-based evidence. Do not modify `CodexPrompt.md`, `PROJECT_REQUIREMENTS.md`, or `PROJECT_STATUS.md`.

## Files / Components to Inspect

- `src/AgentBridge.Abstractions/Interfaces/IAgentAdapter.cs`
- `src/AgentBridge.Abstractions/Interfaces/IRetryPolicy.cs`
- `src/AgentBridge.Core/Orchestration/AgentOrchestrator.cs`
- `src/AgentBridge.Core/Retry/ExponentialBackoffRetryPolicy.cs`
- `src/AgentBridge.Fakes/*`
- `src/AgentBridge.UIAutomation/Adapters/DesktopAgentAdapterBase.cs`
- `src/AgentBridge.App/Program.cs`
- Core and integration test harnesses/tests

## Tests Required

Add focused tests that prove:

1. Non-dry start with simulation-only adapters enters `Error`, starts no watcher, calls no agent operation, and cannot claim delivery.
2. Dry-run start with simulation-only adapters remains valid.
3. Restart from persisted `WaitingForCodex` enters `Error` without invoking Codex.
4. Restart from persisted `WaitingForClaude` enters `Error` without invoking Claude.
5. Maximum-iteration cutoff leaves both watchers stopped and repeated `StopAsync` remains safe; Start-after-Stop still works if it worked before.
6. A boolean condition returning `false` is retried exactly within configured limits and then returns false.
7. A boolean condition that becomes true succeeds after the expected number of attempts/backoff delays.
8. Cancellation interrupts condition retry promptly.
9. `SendMessageAsync` is still attempted at most once per invocation.

Use deterministic time/test doubles where practical. Do not weaken existing assertions or remove existing tests.

## Validation

Run and record exact results for:

```powershell
dotnet restore AgentBridge.slnx --disable-parallel
dotnet build AgentBridge.slnx --no-restore --disable-build-servers --verbosity minimal -m:1
dotnet test AgentBridge.slnx --no-build --no-restore --disable-build-servers -m:1
dotnet build AgentBridge.slnx -c Release --no-restore --disable-build-servers --verbosity minimal -m:1
dotnet test AgentBridge.slnx -c Release --no-build --no-restore --disable-build-servers -m:1
git status --short
git diff --check
dotnet list AgentBridge.slnx package --vulnerable --include-transitive
```

If a command does not complete or does not print a final success/exit result, report it as unverified rather than passed.

## Constraints

- Do not implement conversation discovery, input-box discovery, message sending, accessibility-tree warm-up, WPF, dashboard, tray, notifications, installer, or protocol-path validation in this iteration.
- Do not add new unrelated services, abstractions, documentation plans, or speculative features.
- Do not make Core depend on Fakes, UIAutomation, Infrastructure, or App.
- Do not retry the actual message-send operation.
- Do not solve safety by forcing `DryRun=true` silently; invalid non-dry composition must be explicit and observable.
- Do not commit, push, configure a remote, or alter Git history.
- Do not modify the three Codex-owned management files.

## Definition of Done

- All four defects are corrected with targeted regression tests.
- A non-dry run can never report delivery through a fake or stub adapter.
- The two unsupported restart states fail closed rather than stall or resend.
- All stop paths actually stop watchers and cancel the run.
- False readiness/discovery results receive bounded retries while message send remains single-attempt.
- Debug and Release restore/build/test gates complete successfully with explicit totals and no warnings.
- No existing test is removed or weakened; no unrelated feature is added.
- Repository hygiene and vulnerability checks remain clean.
- `ClaudeResultReport.md` exists and accurately reports files, commands/results, assumptions, and remaining work.

## Notes

Protocol-path containment, the WPF skeleton, and the missing architecture guard remain tracked but are deliberately deferred to keep this iteration coherent. Real UI Automation is not authorized until this corrective gate is approved.

## Timestamp

2026-09-02T18:44:13+03:30
