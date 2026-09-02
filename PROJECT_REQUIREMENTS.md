# Agent Bridge Project Requirements

## Audit Baseline

- Last audit: 2026-09-02 (Asia/Tehran)
- Evidence: repository contents, project references, source/tests, independent Debug restore/build/test, Git status, and NuGet vulnerability audit.
- Claude's narrative report was supplied in chat, but the protocol-owned `ClaudeResultReport.md` file is absent. Claims were therefore validated directly where possible.
- Detailed product/UX requirements remain provisional where noted; do not silently invent them.

## Requirements Checklist

| ID | Requirement | Status | Implementation / Evidence | Tests | Risk / Notes |
|---|---|---|---|---|---|
| R-001 | Buildable, testable Windows desktop solution with explicit boundaries. | Implemented | Nine projects build and `AgentBridge.App` is a real WPF WinExe over the existing service boundaries. | Debug/Release builds plus real-window smoke tests. | Low residual UI risk. |
| R-002 | Separate UI, orchestration, state, file monitoring, persistence, configuration, logging, notifications, Git, UI Automation, adapters, and tests. | Verified | Abstractions/Core/Infrastructure/UIAutomation/Fakes/App separation is coherent. | Automated production-project reference guard passes. | Low residual risk. |
| R-003 | Explicit orchestration state machine with valid transitions and terminal/error states. | Implemented | `BridgeStateMachine` and transition table exist. | State-machine and orchestration tests pass. | Recovery behavior still has defects under R-009/R-016. |
| R-004 | Safely monitor `CodexPrompt.md` and `ClaudeResultReport.md` and act only in the expected phase. | Implemented | Real per-file watchers plus orchestrator state/hash checks. | Unit and integration coverage passes. | High; continue regression coverage. |
| R-005 | Detect stable content and tolerate partial writes, locks, replacement, and rapid writes. | Verified | Debounce, repeated hash stability, retries, create/rename handling. | Relevant real-file tests pass. | Residual platform timing risk. |
| R-006 | Suppress duplicate events/content using stable content identity. | Verified | Watcher emission hash plus persisted orchestrator consumption hashes. | Duplicate/rewrite/burst tests pass. | Preserve catch-up semantics. |
| R-007 | Prevent concurrent or duplicate orchestration operations. | Implemented | Single orchestration semaphore and phase/hash guards. | Concurrent-event test passes. | Fire-and-forget handler lifetime still needs later scrutiny. |
| R-008 | Enforce maximum iterations and pause/resume/stop safely. | Verified | All stop paths cancel the run and stop watchers; Stop pre-cancels in-flight agent work. | Cutoff cleanup, repeated stop, restart, pause/resume, and prompt cancellation tests pass. | Medium residual concurrency risk. |
| R-009 | Persist sufficient state atomically for safe restart/recovery. | Verified | Atomic JSON state and corruption backup exist. Pending pre-action states fail closed instead of stalling or resending. | Clean-stop, corruption, ambiguous-processing, and pre-action recovery tests pass. | Schema migration remains future debt. |
| R-010 | Provide deterministic fake agent adapters before real desktop automation. | Verified | Configurable Claude/Codex fakes exist. | Core and full integration tests use them successfully. | Fakes must never masquerade as real delivery. |
| R-011 | Dashboard reports state, iteration, activity, and actionable errors without business logic. | Implemented | WPF dashboard binds to `MainWindowViewModel` and the existing service contracts; Live capability remains visibly locked. | Real-window rendering and accessibility-tree QA passed. | Advanced filtering/recovery timeline remains optional follow-up. |
| R-012 | Validated configuration for paths, timing, retry, timeout, and iteration limits. | In Progress | Protocol paths are now constrained to simple project-root filenames and project validation honors configured names. Watcher options are still snapshotted at App boot. | Validation and containment tests pass. | Medium. |
| R-013 | Claude Desktop UI Automation using semantic selectors/readiness. | In Progress | Process/window activation and diagnostics exist; conversation/input/send are explicit stubs. | No automated selector/delivery tests. | Critical future milestone. |
| R-014 | ChatGPT Desktop/Codex UI Automation using semantic selectors/readiness. | In Progress | Same partial adapter foundation as R-013. | No automated selector/delivery tests. | Critical future milestone. |
| R-015 | Verify real message delivery rather than assuming input succeeded. | Not Started | Real delivery remains absent. Capability checks now prevent fake/stub adapters from running live or claiming delivery. | Non-dry simulation rejection and single-send-attempt tests pass. | Critical future UI Automation milestone. |
| R-016 | Bounded retry, timeout, cancellation, and recovery without inconsistent state. | Verified | False results and transient exceptions use bounded condition retry; send remains single-attempt; Stop cancels in-flight work. | Retry budget, eventual success, cancellation, timeout, and send-at-most-once tests pass. | Re-audit with real adapters. |
| R-017 | Structured logging/diagnostics with sensitive-content protection by default. | Implemented | Daily logs and diagnostics exist. | Logging tests pass. | Re-audit before real prompt delivery. |
| R-018 | Avoid secrets, unsafe execution, path traversal, and excess privilege. | Verified | Git uses argument lists/read-only commands; protocol filenames cannot escape the project root; no secrets or known vulnerable packages found. | Git non-mutation and unsafe-path tests pass. | Re-audit real UI Automation launch/send paths. |
| R-019 | Notifications and tray behavior. | Implemented | NotifyIcon tray menu, status tooltip, balloon notifications, minimize-to-tray, and existing-instance activation are wired. | Real-process tray/minimize and second-launch activation QA passed. | Modern App SDK toast packaging remains a release enhancement. |
| R-020 | Meaningful unit, integration, and end-to-end coverage. | Verified | 48 core + 43 infrastructure + 8 integration tests. | All 99 independently pass in Debug and Release. | Real UI Automation tests remain future work. |
| R-021 | Packaging/installer and operator/developer documentation. | In Progress | Architecture, future UI, implementation status, and UI Automation notes exist; release/operator docs and installer absent. | None | Release-stage work. |
| R-022 | Repository hygiene; ignore generated output and exclude secrets/debug artifacts. | Verified | One baseline commit exists, no remote is configured, generated output is ignored, and no large unexpected files were found. | `git status`, `git check-ignore`, and `git diff --check` inspected. | Commit `5ac98c5` was created outside the current Codex implementation; current changes remain uncommitted. |
| R-023 | Maintain `ClaudeResultReport.md` / `CodexPrompt.md` ownership protocol. | In Progress | Codex prompt exists and management files were preserved. | Manual audit. | `ClaudeResultReport.md` is missing and must be created by Claude next iteration. |

## Milestone Gate Status

| Milestone | Status | Gate Note |
|---|---|---|
| 1 — Architecture and skeleton | Verified | Architecture, dependency guard, WPF shell, and production composition pass. |
| 2 — Core state machine | Verified | Transition and recovery behavior pass targeted tests. |
| 3 — File watcher/stability | Verified | Stability, deduplication, locking, pause catch-up, and cleanup pass. |
| 4 — Persistent state | Verified | Atomic storage and fail-closed recovery pass. |
| 5 — Fake adapters | Verified | Test simulation is supported and cannot masquerade as live delivery in production composition. |
| 6 — Core orchestration tests | Verified | 99 tests pass in Debug and Release. |
| 7 — GUI dashboard | Verified | Approved design translated into functional light/dark Dashboard, Activity, Diagnostics, Settings, Setup, and Recovery surfaces. |
| 8 — Complete Dry Run desktop experience | Verified | Wizard, guarded reset, tray, notifications, start-minimized behavior, and single-instance activation are operational. |
| 9–17 | Not Started / partial foundation only | Next critical gate is verified real UI Automation delivery. |

## Known Ambiguities

- Exact supported Windows and Claude/ChatGPT Desktop versions.
- Localization scope and final high-contrast validation.
- Reliable conversation identity and observable proof of message delivery.
- Persistence retention/privacy/encryption expectations.
- Packaging/update mechanism and performance targets.

These do not block the current fail-safe lifecycle correction. They must be resolved before their affected milestones.
