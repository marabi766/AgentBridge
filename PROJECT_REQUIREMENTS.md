# Agent Bridge Project Requirements

## Audit Baseline

- Last audit: 2026-09-02 (Asia/Tehran)
- Evidence: repository contents, project references, source/tests, independent Debug restore/build/test, Git status, and NuGet vulnerability audit.
- Claude's narrative report was supplied in chat, but the protocol-owned `ClaudeResultReport.md` file is absent. Claims were therefore validated directly where possible.
- Detailed product/UX requirements remain provisional where noted; do not silently invent them.

## Requirements Checklist

| ID | Requirement | Status | Implementation / Evidence | Tests | Risk / Notes |
|---|---|---|---|---|---|
| R-001 | Buildable, testable Windows desktop solution with explicit boundaries. | In Progress | Nine projects build, but `AgentBridge.App` is a console host rather than the required WPF shell. | Debug build verified; Release gate incomplete. | Medium |
| R-002 | Separate UI, orchestration, state, file monitoring, persistence, configuration, logging, notifications, Git, UI Automation, adapters, and tests. | Implemented | Abstractions/Core/Infrastructure/UIAutomation/Fakes/App separation is generally coherent. | No architecture guard test exists. | High until dependency guard is added. |
| R-003 | Explicit orchestration state machine with valid transitions and terminal/error states. | Implemented | `BridgeStateMachine` and transition table exist. | State-machine and orchestration tests pass. | Recovery behavior still has defects under R-009/R-016. |
| R-004 | Safely monitor `CodexPrompt.md` and `ClaudeResultReport.md` and act only in the expected phase. | Implemented | Real per-file watchers plus orchestrator state/hash checks. | Unit and integration coverage passes. | High; continue regression coverage. |
| R-005 | Detect stable content and tolerate partial writes, locks, replacement, and rapid writes. | Verified | Debounce, repeated hash stability, retries, create/rename handling. | Relevant real-file tests pass. | Residual platform timing risk. |
| R-006 | Suppress duplicate events/content using stable content identity. | Verified | Watcher emission hash plus persisted orchestrator consumption hashes. | Duplicate/rewrite/burst tests pass. | Preserve catch-up semantics. |
| R-007 | Prevent concurrent or duplicate orchestration operations. | Implemented | Single orchestration semaphore and phase/hash guards. | Concurrent-event test passes. | Fire-and-forget handler lifetime still needs later scrutiny. |
| R-008 | Enforce maximum iterations and pause/resume/stop safely. | In Progress | State transitions exist, but maximum-iteration cutoff sets `Stopped` without stopping watchers; later `StopAsync` returns early. | Existing max test misses resource cleanup. | Critical corrective work required. |
| R-009 | Persist sufficient state atomically for safe restart/recovery. | In Progress | Atomic JSON state and corruption backup exist. `WaitingForCodex`/`WaitingForClaude` are classified resumable but no action resumes, causing a permanent stall. | Clean-stop and ambiguous-processing tests pass; pre-action recovery tests absent. | Critical corrective work required. |
| R-010 | Provide deterministic fake agent adapters before real desktop automation. | Verified | Configurable Claude/Codex fakes exist. | Core and full integration tests use them successfully. | Fakes must never masquerade as real delivery. |
| R-011 | Dashboard reports state, iteration, activity, and actionable errors without business logic. | Not Started | Backend status model only; console diagnostic host is not the product UI. | None | UX details remain ambiguous. |
| R-012 | Validated configuration for paths, timing, retry, timeout, and iteration limits. | In Progress | Settings validation exists, but configured protocol filenames permit rooted/traversal paths and project validation hard-codes default filenames. Watcher options are snapshotted at App boot. | Basic validation tests pass; containment tests absent. | High; schedule after current safety gate. |
| R-013 | Claude Desktop UI Automation using semantic selectors/readiness. | In Progress | Process/window activation and diagnostics exist; conversation/input/send are explicit stubs. | No automated selector/delivery tests. | Critical future milestone. |
| R-014 | ChatGPT Desktop/Codex UI Automation using semantic selectors/readiness. | In Progress | Same partial adapter foundation as R-013. | No automated selector/delivery tests. | Critical future milestone. |
| R-015 | Verify real message delivery rather than assuming input succeeded. | Not Started | Real delivery is absent. App currently wires fakes, so `DryRun=false` can falsely report success. | No composition safety test. | Critical corrective work required before UI automation. |
| R-016 | Bounded retry, timeout, cancellation, and recovery without inconsistent state. | In Progress | Timeout and exception retry exist. Boolean `false` readiness/discovery results return immediately and are not retried despite documented claims. | Exception/backoff/timeout tests pass; false-result retry tests absent. | High corrective work required. |
| R-017 | Structured logging/diagnostics with sensitive-content protection by default. | Implemented | Daily logs and diagnostics exist. | Logging tests pass. | Re-audit before real prompt delivery. |
| R-018 | Avoid secrets, unsafe execution, path traversal, and excess privilege. | In Progress | Git uses argument lists and read-only commands; no secrets found; NuGet reports no known vulnerabilities. Protocol filename containment is not enforced. | Git non-mutation tests pass; security path tests absent. | High. |
| R-019 | Notifications and tray behavior. | Not Started | Null notification implementation only. | None | Low until core is reliable. |
| R-020 | Meaningful unit, integration, and end-to-end coverage. | Implemented | 38 core + 32 infrastructure + 8 integration tests. | All 78 independently passed in Debug. | Missing architecture, composition-safety, recovery-stall, and cutoff-cleanup tests. |
| R-021 | Packaging/installer and operator/developer documentation. | In Progress | Architecture, future UI, implementation status, and UI Automation notes exist; release/operator docs and installer absent. | None | Release-stage work. |
| R-022 | Repository hygiene; ignore generated output and exclude secrets/debug artifacts. | Verified | Git initialized, no remote/commit, generated output ignored, no large unexpected files found. | `git status`, `git check-ignore`, and `git diff --check` inspected. | All files remain untracked because no baseline commit was authorized. |
| R-023 | Maintain `ClaudeResultReport.md` / `CodexPrompt.md` ownership protocol. | In Progress | Codex prompt exists and management files were preserved. | Manual audit. | `ClaudeResultReport.md` is missing and must be created by Claude next iteration. |

## Milestone Gate Status

| Milestone | Status | Gate Note |
|---|---|---|
| 1 — Architecture and skeleton | In Progress | Buildable structure exists; required WPF shell and architecture guard test are missing. |
| 2 — Core state machine | Implemented, not approved | Passing tests; blocked by recovery/lifecycle findings. |
| 3 — File watcher/stability | Implemented, not approved | Strong coverage; max-cutoff resource leak must be fixed. |
| 4 — Persistent state | Implemented, not approved | Atomic storage exists; restart semantics are incomplete. |
| 5 — Fake adapters | Implemented, not approved | Must add capability/simulation safety so fakes cannot claim real delivery. |
| 6 — Core orchestration tests | Implemented, not approved | 78 pass; critical negative cases missing. |
| 7–17 | Not Started / partial foundation only | No later milestone is authorized until the corrective gate passes. |

## Known Ambiguities

- Exact supported Windows and Claude/ChatGPT Desktop versions.
- Final UI framework details, design, accessibility, and localization.
- Reliable conversation identity and observable proof of message delivery.
- Persistence retention/privacy/encryption expectations.
- Packaging/update mechanism and performance targets.

These do not block the current fail-safe lifecycle correction. They must be resolved before their affected milestones.
