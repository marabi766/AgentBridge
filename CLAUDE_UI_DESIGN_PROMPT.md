# Claude Design Prompt

Read the attached `UI_DESIGN_BRIEF.md` completely and use it as the authoritative product and safety brief.

Design a high-fidelity Windows desktop UI/UX system for Agent Bridge. This product supervises a long-running automation cycle between Claude Code and Codex, so operational clarity, safe controls, truthful delivery status, recoverability, and diagnostics matter more than decorative styling.

Produce a coherent design covering:

1. First-run setup wizard
2. Main dashboard
3. Activity/log viewer
4. Agent diagnostics
5. Settings
6. Error and recovery flows
7. System-tray menu
8. Dry Run and Live mode variants

Start with the main dashboard and establish the visual system from it. Then apply the same system to the remaining screens. Include both light and dark dashboard examples.

The dashboard must expose the exact operational data listed in the brief and must distinguish every state in the state machine. Dry Run must remain globally visible. Live mode must be unavailable when the selected adapters cannot verify delivery. Never represent an attempted input action as successful message delivery without verification.

Use a professional, restrained Windows-tooling aesthetic. Favor clear hierarchy and compact information density. Avoid generic AI gradients, excessive card layouts, ornamental glow, and chat-product styling. Meet WCAG 2.1 AA contrast, provide keyboard/focus behavior, and communicate status with text/iconography as well as color.

For every screen, include the important normal, disabled, loading, empty, warning, error, and long-running states. Show realistic content rather than lorem ipsum. Include component variants, design tokens, interaction annotations, responsive behavior for narrower desktop windows, and developer-handoff notes suitable for a future WPF implementation.

Do not modify the backend workflow or assume that real conversation discovery and message delivery already exist. If you propose enhancements beyond the brief, separate them clearly as optional ideas.
