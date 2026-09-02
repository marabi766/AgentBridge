# UI Automation — findings and current state

## Status: architecture only, real selectors deferred

Per the backend-first phase, this document records what was **actually
verified** against the real, currently-installed Claude Desktop and ChatGPT
Desktop on this machine, and precisely what is and isn't implemented in
`AgentBridge.UIAutomation`. Nothing below is guessed.

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

**Implication for the real implementation (next phase):** the adapter must
issue a "warm-up" query (e.g. read a cheap property from the main window)
and then wait/retry — a single query-and-give-up will see an empty tree and
incorrectly report the app as not ready. This must be built into
`IsReadyAsync`/`FindConversationAsync` with a bounded retry, not a fixed
sleep.

## Selector strategy for the next phase

Given the tree is generic Chromium accessibility roles (not custom
AutomationIds), the layered-selector priority from the project spec
(AutomationId -> ControlType -> Name -> structural relationship -> coordinate
fallback) will mostly skip straight to **ControlType + Name + structural
position**, since these Electron apps do not appear to assign custom
AutomationIds to their React-rendered content. This needs a full, patient
tree walk (with the warm-up above) to identify the actual input textbox and
conversation container by role and accessible name — that is real UI
Automation implementation work and is explicitly out of scope for this
phase. `AgentBridge.UIAutomation.Locators.ILocators` (`IWindowLocator`,
`IConversationLocator`, `IInputLocator`, `IMessageSender`) establishes where
that logic will live; no concrete implementation exists yet.

## What is implemented right now in `DesktopAgentAdapterBase`

| Method | Status | Notes |
|---|---|---|
| `IsApplicationRunningAsync` | **Real** | `Process.GetProcessesByName` + `MainWindowHandle != 0`. |
| `LaunchApplicationAsync` | **Real** | `Process.Start` on a configured executable path. Untested against the MSIX-packaged executables specifically (should work — they're signed Win32 EXEs, not UWP — but not yet verified end-to-end). |
| `ActivateAsync` | **Real** | `FlaUI Window.SetForeground()` on the main window handle. |
| `IsReadyAsync` | **Real, shallow** | Checks `IsEnabled` / `!IsOffscreen` on the main window only. Does **not** yet do the warm-up-and-wait needed to know the web content has actually rendered. |
| `GetDiagnosticsAsync` | **Real** | Dumps a 3-level-deep automation tree per running process — directly reuses the technique validated above. This is the diagnostics capability the spec asks to keep available regardless of automation-phase progress. |
| `FindConversationAsync` | **Not implemented** | Returns `false` and logs a clear message. No selector logic exists yet. |
| `FindInputBoxAsync` | **Not implemented** | Same. |
| `SendMessageAsync` | **Not implemented** | Same — never sends anything, never pretends to. |

None of the "not implemented" methods throw — they return `false` with a
warning log, matching the `IAgentAdapter` contract ("must not throw for
expected negative outcomes"). `AgentOrchestrator` treats a `false` from any
of these as a failed invocation -> `Error` state with a descriptive
`LastError`, exactly like a real failure would be handled. Use **Dry Run**
mode until the next phase implements real message delivery.

## Known limitation to carry into the next phase

Because conversation/input-box discovery isn't implemented, there is
currently no way to target a *specific* conversation within either app (the
"conversation identifier" fields in `BridgeConfiguration` are accepted but
unused). Section 30 of the original spec anticipates this may need a
user-driven "select/confirm the target conversation" setup step if automatic
identification proves unreliable once the real tree-walking is built — kept
as an open design question, not resolved here.
