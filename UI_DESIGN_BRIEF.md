# Agent Bridge UI Design Brief

## Product Purpose

Agent Bridge is a Windows desktop control surface for a supervised automation loop between Claude Code and Codex. It observes two Markdown protocol files in a selected Git project, maintains a recoverable state machine, and eventually delivers the next instruction through verified Windows UI Automation.

The interface must make automation state, safety, progress, and failures obvious. It must never imply that a message was delivered unless delivery was verified by a real adapter.

## Design Scope

Design a production-ready Windows desktop application for these surfaces:

1. First-run setup wizard
2. Main dashboard
3. Settings
4. Activity log
5. Agent diagnostics
6. Error and recovery states
7. System-tray menu and compact status treatment

Do not design source-code editors, chat clients, or an AI conversation interface. Agent Bridge is an operational dashboard and safety controller.

## Information Architecture

Use a persistent desktop navigation structure with these primary destinations:

- Dashboard
- Activity
- Diagnostics
- Settings

The current automation state and Dry Run status must remain visible from every primary screen.

## Main Dashboard

Show:

- Current state and plain-language status text
- Current iteration and maximum iterations
- Claude status
- Codex status
- Dry Run or Live mode
- Last action
- Last error, when present
- Last Claude report update
- Last Codex prompt update
- Git branch
- Git working-tree summary
- Recent activity preview

Primary controls:

- Start
- Pause
- Resume
- Stop

Only actions valid for the current state should be enabled. Destructive or recovery actions require clear confirmation.

## State Model

The interface must visually distinguish:

- Idle
- Waiting for Claude report
- Claude report detected
- Waiting for Codex
- Codex processing
- Waiting for Codex prompt
- Codex prompt detected
- Waiting for Claude
- Claude processing
- Paused
- Stopped
- Error

Do not reduce these to a vague binary “running/not running” display. A compact cycle visualization is encouraged if it improves comprehension without dominating the dashboard.

## Safety Requirements

- Dry Run must be unmistakable and visible globally.
- Live mode must use a more cautionary treatment and require explicit user intent.
- If adapters cannot verify real delivery, Live mode must be unavailable with an explanatory message.
- Never use a green success state merely because a click or keystroke was attempted.
- Error states must state what failed, whether an action may have been partially completed, and what the user can safely do next.
- Reset State must be presented only in recovery context and must explain that iteration/hash progress will be discarded while protocol files remain untouched.
- Do not expose full prompt/report contents in routine logs or dashboard cards.

## Setup Wizard

Recommended steps:

1. Welcome and safety explanation
2. Select project folder
3. Validate folder and Git repository
4. Verify `ClaudeResultReport.md` and `CodexPrompt.md`
5. Detect Claude Desktop
6. Detect ChatGPT Desktop/Codex
7. Select or confirm target conversations
8. Test accessibility/readiness
9. Configure iteration, timeout, retry, and notification settings
10. Run a Dry Run validation
11. Review and finish

The wizard must support retry, back navigation, partial completion, and precise remediation messages. Steps dependent on unfinished UI Automation should have honest unavailable/not-yet-configured states rather than fake success.

## Settings

Group settings by:

- Project and protocol files
- Claude Desktop
- ChatGPT Desktop/Codex
- Automation safety
- Timing and retries
- Notifications
- Logging and diagnostics

Important controls include project folder picker, application executable selection, conversation identifiers, maximum iterations, timeout, retry count, debounce/stability timing, Dry Run, notifications, startup behavior, and message templates.

Show validation beside the affected field. Invalid settings must never partially save.

## Activity and Diagnostics

Activity view:

- Timestamp
- Severity
- Category
- Concise message
- Expandable exception details
- Date and severity filters
- Copy/export affordance that warns about sensitive information

Diagnostics view:

- Claude process/window/readiness status
- Codex process/window/readiness status
- Test Connection actions
- Automation-tree diagnostics in a readable monospaced viewer
- Copy diagnostics action
- Clear distinction among Not Running, Running, Ready, Unreachable, and Error

## Required Interaction States

Design normal, hover, focus, disabled, loading, success, warning, error, empty, disconnected, and long-running states. Include behavior for long error text, missing Git branch, unavailable timestamps, high iteration counts, narrow desktop windows, and stale status information.

## Visual Direction

- Professional Windows desktop tooling rather than a consumer chat aesthetic
- Calm, information-dense, and operationally clear
- Strong hierarchy with restrained color
- Avoid decorative AI gradients, glowing effects, oversized cards, and excessive rounded containers
- Support light and dark themes
- Use accessible contrast and do not communicate status by color alone
- Prefer familiar Windows interaction patterns and keyboard navigation
- Primary UI copy should be English and layout should remain localization-ready

## Accessibility

- Target WCAG 2.1 AA contrast
- Visible keyboard focus
- Logical tab order
- Minimum practical pointer targets
- Screen-reader labels for state, progress, buttons, and icons
- Reduced-motion behavior
- Text alternatives for color-coded status

## Deliverables Requested from Claude Design

1. High-fidelity designs for all primary screens
2. Light and dark theme examples for the dashboard
3. First-run wizard flow
4. Error/recovery and Dry Run/Live mode variants
5. Reusable component inventory with variants and states
6. Typography, spacing, color, icon, elevation, and motion tokens
7. Interaction annotations and keyboard behavior
8. Empty/loading/error edge cases
9. Developer handoff notes suitable for later WPF implementation

## Technical Constraints

- The future UI is a thin client over service interfaces; business logic must not be placed in view code.
- The current application is a console host. The UI design must not assume existing XAML or existing visual components.
- Real message delivery and conversation selection are not yet implemented.
- The design must accommodate explicit failure and incomplete-capability states.
- Do not redesign the backend workflow or invent new product features without marking them as optional proposals.

## Available Status Data

The dashboard can bind to these fields:

- `CurrentState`
- `StatusText`
- `CurrentIteration`
- `MaximumIterations`
- `ClaudeStatus`
- `CodexStatus`
- `IsRunning`
- `IsPaused`
- `LastAction`
- `LastError`
- `LastClaudeReportUpdateUtc`
- `LastCodexPromptUpdateUtc`
- `GitBranch`
- `GitWorkingTreeSummary`
- `DryRun`
- `GeneratedAtUtc`

Use realistic sample values in the design and show how unknown or unavailable values appear.
