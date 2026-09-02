# UI requirements and implementation status

> The Dry Run WPF product now exists in `AgentBridge.App`. Dashboard, Activity,
> Diagnostics, Settings, Setup, guarded recovery, tray/notifications,
> single-instance activation, and light/dark themes are functional. Sections
> below remain the contract for verified Live delivery and optional richer views.

The backend built in this phase is fully independent of any presentation
technology. This document is the contract the future dashboard/tray/settings/
wizard UI is built against — everything it needs already exists behind these
interfaces (all in `AgentBridge.Abstractions.Interfaces`).

## Status information the UI needs

All of it comes from one call: `IOrchestratorService.GetStatusAsync()` ->
`BridgeStatusView`. Subscribe to `IOrchestratorService.StatusChanged` for push
updates on every state transition (git/agent-liveness fields on that pushed
snapshot reflect the last time `GetStatusAsync` was called, not a live poll —
the UI should call `GetStatusAsync` on an interval, e.g. every few seconds, to
keep git/agent status fresh, and rely on the event for instant state-machine
updates).

`BridgeStatusView` fields, and where the dashboard mockup (project spec
section 20) should show each:

| Field | Dashboard element |
|---|---|
| `CurrentState` + `StatusText` | "Current State" — always the unambiguous text (`BridgeStateDescriptions`), never a raw enum, never "Running" |
| `CurrentIteration` / `MaximumIterations` | "Current Iteration" / "Maximum Iterations" |
| `ClaudeStatus` / `CodexStatus` (`AgentStatus`) | "Claude Status" / "Codex Status" |
| `IsRunning` / `IsPaused` | Enable/disable Start/Pause/Resume/Stop buttons |
| `LastAction` | "Last Action" |
| `LastError` | "Last Error" — only non-null when something is actually currently wrong (cleared automatically on a successful `Start`) |
| `LastClaudeReportUpdateUtc` / `LastCodexPromptUpdateUtc` | "Last Claude Report Update" / "Last Codex Prompt Update" — convert to local time for display |
| `GitBranch` / `GitWorkingTreeSummary` | "Current Git Branch" / "Working Tree Status" |
| `DryRun` | A persistent banner/badge — the user must always be able to tell at a glance whether real sends are happening |

## Commands

All from `IOrchestratorService`:

- `StartAsync` / `PauseAsync` / `ResumeAsync` / `StopAsync` — map directly to
  the spec's Start/Pause/Resume/Stop buttons and the tray menu. All are
  idempotent no-ops when called from an inapplicable state (e.g. `Pause` while
  already `Paused` just logs and returns) — the UI does not need to
  pre-validate button availability beyond what `IsRunning`/`IsPaused` already
  gate.
- `TestClaudeConnectionAsync` / `TestCodexConnectionAsync` — "Test Claude
  Connection" / "Test Codex Connection" buttons.
- `ResetStateAsync` — the recovery action for when `CurrentState == Error`
  (see "Recovery" below). Not exposed as a normal button; surface it
  specifically when the bridge is in `Error`, with a clear warning about what
  it discards (iteration count, hashes — a genuinely fresh start).

## Settings

`ISettingsService.GetCurrentAsync` / `ValidateAsync` / `UpdateAsync`. Every
field in `BridgeConfiguration` is already validated (`SettingsService`) before
persistence — the UI never needs its own duplicate validation logic, but
should surface `SettingsValidationResult.Errors` directly next to the
relevant fields. `UpdateAsync` **never partially applies** an invalid
configuration — a failed validation leaves the previously-saved settings
completely untouched (see `Never silently overwrite` in the original spec;
`SettingsServiceTests.UpdateAsync_InvalidConfiguration_NeverPersists` proves
this).

Fields that need dedicated setup UI beyond a plain text box, per the
original spec's Settings section:

- `ProjectPath` — folder picker + live validation via `IProjectService
  .ValidateProjectPathAsync` (shows whether it's a git repo, whether the two
  markdown files already exist).
- `ClaudeExecutablePath` / `ChatGptExecutablePath` — file pickers.
- `ClaudeInstructionTemplate` / `CodexInstructionTemplate` — multi-line text
  editors with the placeholder list from `TemplateVariableNames` shown as a
  reference/autocomplete.
- `ClaudeConversationIdentifier` / `CodexConversationIdentifier` — currently
  accepted but unused by the backend (see `UI_AUTOMATION.md`'s "Known
  limitation"); until real conversation discovery lands, keep this field but
  don't imply it does anything yet.
- `DryRun` — should be prominent, not buried; this is the safety switch for
  testing the whole pipeline without touching real Claude/ChatGPT windows.

## Setup wizard

`IProjectService.ValidateProjectPathAsync` returns everything step 1-2 of the
wizard (project folder + git verification) needs in one call
(`ProjectValidationResult`: `PathExists`, `IsGitRepository`,
`ClaudeReportFileExists`, `CodexPromptFileExists`, `Errors`, `Warnings`).
Steps 3-8 (detect apps, test UI automation, select conversations) depend on
the real `UIAutomation` adapters, which are not implemented yet — the wizard
can be scaffolded now against `TestClaudeConnectionAsync`/
`TestCodexConnectionAsync` (they already return an honest `false` against the
current stub adapters) but will only become meaningful once
`FindConversationAsync`/`FindInputBoxAsync` are real. Step 10 (test the file
watcher) and step 11 (Dry Run validation) can be built now — nothing blocks
them.

## Logs

`ILogService.GetAvailableLogDatesAsync` / `ReadLogAsync(date)` /
`TailAsync(maxEntries)`. Each `LogEntry` has `TimestampUtc`, `Level`,
`Category`, `Message`, and an optional `Exception` string (multi-line stack
traces are already reassembled by `FileLogService`'s parser — the log viewer
can just render `Exception` as a collapsible block). Logs live at
`%LOCALAPPDATA%\AgentBridge\logs\<yyyy-MM-dd>.log`, one file per day.

## Diagnostics

`IAgentDiagnosticsService.GetClaudeDiagnosticsAsync` /
`GetCodexDiagnosticsAsync` return the live automation-tree dump described in
`UI_AUTOMATION.md` (process info + a 3-level tree). A diagnostics screen can
be built today showing this text as-is; it becomes much more useful once real
selector logic exists and can annotate which elements it actually matched.

## Recovery

When `CurrentState == Error`, the UI must show `LastError` prominently and
offer exactly one clear path forward: `ResetStateAsync`, described honestly
as "discard current progress and start fresh" (it clears iteration count and
both file hashes; it does not touch `ClaudeResultReport.md`/`CodexPrompt.md`
on disk). Do not offer a generic "Resume" from `Error` — the backend
deliberately refuses to guess whether it's safe (see `ARCHITECTURE.md`'s
"Safe recovery" section) and there is no `Resume`-from-`Error` transition in
the state machine.

## Explicitly NOT built yet (do not assume otherwise)

- Packaged modern Windows toast notifications. The current desktop build uses
  native tray balloon notifications through `DesktopNotificationService`.
- Start-on-Windows-login registration. Start-minimized and tray behavior are implemented.
- The setup wizard's UI Automation-dependent steps (3-9 in the original
  spec's sequence).
- Optional richer Activity filtering/export and recovery timeline variants from
  the approved design. Light/dark theme switching is implemented.
