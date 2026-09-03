# UI Automation — verified delivery implementation

## Status: semantic selectors implemented; live canary still requires user approval

This document records what was verified against the installed Claude Desktop
and ChatGPT Desktop applications and the fail-closed delivery contract now
implemented in `AgentBridge.UIAutomation`.

## Environment as inspected (2026-09-02)

Both applications are installed as MSIX packages (not plain unpacked EXEs):

| App | Package | Main process | Path |
|---|---|---|---|
| Claude Desktop | `Claude_1.40609.1.0_x64__pzs8sxrjxfjjc` | `Claude.exe` (window title `Claude`) | `C:\Program Files\WindowsApps\Claude_1.40609.1.0_x64__pzs8sxrjxfjjc\app\Claude.exe` |
| ChatGPT Desktop (Codex) | `OpenAI.Codex_26.825.6671.0_x64__2p2nqsd0c76g0` | `ChatGPT.exe` (window title `ChatGPT`) | `C:\Program Files\WindowsApps\OpenAI.Codex_26.825.6671.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe` |

Both are multi-process Electron/Chromium-based apps (many child renderer
processes per app; only one process per app owns the visible main window).
Claude Desktop also spawns a separate native Claude Code CLI process
(`%APPDATA%\Claude\claude-code\<version>\claude.exe`) — that is a different
executable from the desktop shell and not part of this app's automation
surface. ChatGPT Desktop similarly has separate native `codex.exe` /
`codex-code-mode-host.exe` helper processes.

Both main windows have `ClassName = Chrome_WidgetWin_1` (standard Chromium
top-level window class) and use the modern Windows client-area frame
(`WinCaptionButtonContainer`/`WinCaptionButton` for minimize/maximize/close —
these ARE reliably automatable via `ControlType.Button` + `Name` = "Minimize"
/ "Maximize" / "Close").

## Library choice: FlaUI.UIA3

`FlaUI.UIA3` (wrapping the native UI Automation COM API) was added and
verified to work against .NET 10 / this Windows build with zero issues —
`automation.FromHandle(process.MainWindowHandle)` and
`element.FindAllChildren()` both work as expected. No reason found to prefer
raw `System.Windows.Automation` or an alternative library.

## Critical finding: Chromium's accessibility tree is lazy

The first `UIA3Automation` query against either app's window returns almost
nothing below the native window chrome:

```
[Window] Name='Claude' Class='Chrome_WidgetWin_1'
  [Pane] Class='RootView'
    [Pane] Class='NonClientView'
      [Pane] Class='WinFrameView'
        [Pane] Class='WinCaptionButtonContainer'
          [Button] Name='Minimize' ...
          [Button] Name='Maximize' ...
          [Button] Name='Close' ...
        [Pane] Class='ClientView'
          [Pane] Class='View' (x4, all empty)
```

That's it — 2KB of tree, no web content. This is expected Chromium behavior:
the renderer's accessibility tree is only built and exposed once a UI
Automation client "warms it up" by querying it (Chromium calls this enabling
"AXMode complete"). **The second query, made ~2 seconds later against the
same window, returned a 238KB tree** with the full web app content
(conversation list, message bubbles, input area, etc. — all exposed as
generic Chromium accessibility roles like `Document`, `Group`, `Text` rather
than native Win32 controls).

**Implemented response:** conversation discovery performs a first descendant
read and, when the tree is still shallow, waits up to two seconds before a
second read. The orchestrator adds bounded retry around discovery.

## Implemented selector strategy

Given the tree is generic Chromium accessibility roles (not custom
AutomationIds), the layered-selector priority from the project spec
(AutomationId -> ControlType -> Name -> structural relationship -> coordinate
fallback) will mostly skip straight to **ControlType + Name + structural
position**, since these Electron apps do not assign stable AutomationIds to
their React content. The implementation uses:

- an exact configured active-conversation title;
- a visible, enabled title button outside sidebar/folder rows;
- exactly one visible, enabled `Edit` control with a writable `ValuePattern`
  and known editor semantics (`ProseMirror`, `Prompt`, or `Do anything`);
- exactly one enabled `Button` named `Send` with an `InvokePattern`.

No coordinate, clipboard, keyboard-shortcut, fuzzy-title, sidebar-click, or
"first match" fallback exists. Ambiguity returns `false`.

## Delivery verification contract

| Method | Status | Notes |
|---|---|---|
| `IsApplicationRunningAsync` | **Real** | `Process.GetProcessesByName` + `MainWindowHandle != 0`. |
| `LaunchApplicationAsync` | **Real** | `Process.Start` on a configured executable path. Untested against the MSIX-packaged executables specifically (should work — they're signed Win32 EXEs, not UWP — but not yet verified end-to-end). |
| `ActivateAsync` | **Real** | `FlaUI Window.SetForeground()` on the main window handle. |
| `IsReadyAsync` | **Real** | Checks `IsEnabled` / `!IsOffscreen`; deeper readiness is proven during semantic discovery. |
| `GetDiagnosticsAsync` | **Real** | Dumps a 3-level-deep automation tree per running process — directly reuses the technique validated above. This is the diagnostics capability the spec asks to keep available regardless of automation-phase progress. |
| `FindConversationAsync` | **Implemented** | Requires one exact active-title marker matching the configured identifier. |
| `FindInputBoxAsync` | **Implemented** | Requires one writable semantic editor. |
| `SendMessageAsync` | **Implemented** | Sets a draft through `ValuePattern`, invokes one exact Send button once, then requires both an empty input and one additional exact rendered message. |

Expected negative outcomes return `false`. Before Send is invoked, any draft
is cleared on failure or cancellation. After the single invocation, absence
of a positive receipt returns failure and is never represented as delivered.

## Validation performed on 2026-09-03

The read-only probe verified both the active conversation and unique input on
the live installed applications:

- Claude: `Conversation=VERIFIED Input=VERIFIED`
- ChatGPT/Codex: `Conversation=VERIFIED Input=VERIFIED`

A user-approved real one-message canary was performed on 2026-09-03. Claude
received the prompt and began processing it, as confirmed by the user and the
live accessibility tree. The canary exposed two edge cases and the sender was
hardened immediately afterward: it now refuses a non-empty editor, verifies
that setting the draft produced the exact requested value before invoking
Send, and accepts only an exact rendered message or Claude's exact
`You said: <message>` accessibility wrapper as the positive receipt. Dry Run
remains the default.
