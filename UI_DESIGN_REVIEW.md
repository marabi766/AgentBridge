# Agent Bridge UI — Design Review and Engineering Handoff

Date: 2026-09-02  
Source reviewed: `# Agent Bridge UI System.zip` supplied by the user. The archive contained a visual design document, supporting JavaScript, a thumbnail, and an unchanged copy of `UI_DESIGN_BRIEF.md`.

## Decision

**Approved with implementation constraints.** The design is sufficiently complete to guide the WPF product UI. It has strong information hierarchy, comprehensive operational states, explicit recovery language, realistic responsive rules, and unusually good accessibility coverage. Most importantly, it preserves the project's fail-closed safety rule: composed, attempted, simulated, and verified delivery are visually and textually distinct.

The archive is a design/handoff document rather than an executable application prototype. Its examples are product specifications, not claims about currently available backend capabilities.

## Required implementation constraints

- Live mode remains unavailable until both selected adapters expose real delivery and a positive verification mechanism. The dark Live-mode screen is a future-state specification only.
- `Retry step`, one-time timeout extension, manual `Mark as delivered`, wizard progress persistence, and delivery receipts require backend/application contracts not currently present. The UI must not display these as working controls until those contracts exist. Tray behavior and dark-theme switching are now implemented.
- The design's eight visual cycle positions are a presentation grouping over the twelve `BridgeState` values; the state machine remains authoritative.
- The verified-green token is reserved for confirmed file-hash changes or a future verified delivery outcome. Agent process detection or an attempted send is not enough.
- Protocol/report bodies must not be rendered on the dashboard or written to logs.
- Error recovery must remain explicit. State reset is not a normal toolbar action and must describe exactly what is discarded.

## WPF handoff

| Area | Contract |
|---|---|
| Shell | Persistent state, iteration, freshness, and Dry Run banner; navigation rail plus content surface; minimum 620×480 |
| Layout | 16 px page padding, 12 px grid gaps, flat white panels with 1 px strokes and 4 px radius |
| Typography | Segoe UI Variable Text / Segoe UI; 20 px title, 15 px section title, 13 px body, 11 px labels |
| Commands | Bind to `IOrchestratorService`; command availability comes from authoritative status fields |
| Settings | Load/save through `ISettingsService`; invalid settings never partially persist; Live forced unavailable |
| Activity | Read through `ILogService`; newest first; prompt/report content stays redacted |
| Diagnostics | Use `IAgentDiagnosticsService` and connection tests; never infer readiness from window presence alone |
| Accessibility | Text accompanies every status symbol; ≥32 px control targets; keyboard shortcuts; polite status announcements and assertive errors |
| Motion | No motion is required for the first implementation; future motion must honor Windows reduced-motion settings |

## First implementation delivered

The initial WPF shell implements the dashboard, persistent Dry Run banner, command controls, Activity viewer, agent Diagnostics, and atomic Settings editor. Empty project configuration opens Settings on first launch. Live mode is visibly disabled with its reason. Stop requires confirmation and keeps progress. Keyboard shortcuts are F5 (refresh), Ctrl+R (start), Ctrl+P (pause), and Ctrl+Shift+S (stop).

The second increment adds a five-step capability-aware setup wizard, guarded recovery reset, light/dark themes, tray/minimized behavior, native Windows notifications, and single-instance activation. Deferred design surfaces are the optional rich recovery timeline, advanced Activity filtering/export, wizard progress persistence, and all controls that depend on verified desktop delivery.
