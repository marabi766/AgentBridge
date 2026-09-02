# Agent Bridge Project Status

## Acceptance Decision

**NEEDS_FIXES** — substantial backend functionality exists and all 78 Debug tests pass independently, but multiple untested correctness and safety defects prevent approval or progression to real UI Automation.

## Current State

- Current milestone: Corrective quality gate across Milestones 2–6
- Completed/approved milestones: None
- Current objective: Make simulation-vs-real delivery, stop cleanup, restart recovery, and readiness retry behavior fail-safe and test-proven.
- Next planned milestone after approval: Finish Milestone 1 gate items (WPF shell and architecture dependency guard), then re-evaluate sequencing.
- Overall health: Yellow / functional prototype with critical safety gaps
- Next iteration risk: High

## Independently Verified Evidence

- Git repository exists with no commits and no remotes; all implementation files are untracked.
- `dotnet restore AgentBridge.slnx --disable-parallel`: passed.
- Serialized Debug build: passed with 0 warnings and 0 errors.
- Core tests: 38/38 passed.
- Infrastructure tests: 32/32 passed.
- Integration tests: 8/8 passed when isolated; total verified tests: 78.
- NuGet direct/transitive vulnerability audit: no known vulnerable packages from the configured source.
- Generated `bin`/`obj` output is ignored; no large unexpected files or obvious embedded secrets were found.
- Release build/test gate was attempted but did not complete with a usable final result; it remains unverified.
- `ClaudeResultReport.md` is absent despite the protocol and report claim.

## Blocking Findings

1. **False delivery success (Critical):** `AgentBridge.App` registers fake adapters. With `DryRun=false`, those fakes return success and the orchestrator records “Sent instruction” even though no desktop app received anything.
2. **Incomplete restart recovery (Critical):** persisted `WaitingForCodex` and `WaitingForClaude` states are accepted as safe, but `StartAsync` neither resumes the pending invocation nor fails closed. The bridge can remain stuck indefinitely.
3. **Incomplete maximum-iteration stop (High):** `StopForMaxIterationsAsync` transitions to `Stopped` but does not cancel the run or stop watchers. A later `StopAsync` returns immediately because state is already `Stopped`, leaving resources active.
4. **Retry behavior mismatch (High):** retry policy retries exceptions only. Normal `false` responses from readiness/conversation/input discovery are not retried, contradicting the documented bounded-readiness retry behavior needed for lazy Chromium accessibility trees.
5. **Protocol path containment (High):** configurable report/prompt filenames can be rooted or contain traversal segments; computed watcher paths are not proven to remain under `ProjectPath`. Project validation also reports only hard-coded default filenames.
6. **Milestone 1 incompleteness (Medium):** App is a console host, not WPF, and no architecture guard test exists.
7. **Scope control failure (Medium):** Iteration 1 explicitly prohibited functional backend/UI Automation implementation, but the implementation expanded across multiple milestones without authorization.

## Positive Architecture Assessment

- Core has no direct UI Automation dependency.
- Windows automation is isolated behind adapter abstractions.
- Git commands use `ProcessStartInfo.ArgumentList` and are read-only.
- File stability/hash handling and the real-file integration tests are materially stronger than superficial scaffolding.
- No large refactor is warranted; targeted corrections should preserve the current structure.

## Quality Gate

| Area | Result |
|---|---|
| Debug build | Pass |
| Debug tests | Pass — 78/78 |
| Release build/tests | Unverified |
| Architecture | Conditional pass; guard test missing |
| Requirements | Partial; major scope delivered but not approved |
| Security | Fail pending protocol-path containment |
| Reliability | Fail pending four lifecycle/delivery/retry corrections |
| Documentation | Partial; Claude protocol report missing |
| Repository hygiene | Pass for current uncommitted state |

## Next Action

Claude must execute only Iteration 2 in `CodexPrompt.md`. Do not begin real UI Automation, WPF, tray, installer, or new product features until Codex independently verifies the corrective tests and implementation.
