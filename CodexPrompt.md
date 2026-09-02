# Codex Prompt

## Iteration

3

## Decision

BLOCKED

## Objective

Do not make implementation changes yet. The backend corrective gate is approved, and the next source task is the WPF presentation layer. That work is waiting for an approved UI design produced from `UI_DESIGN_BRIEF.md`.

## Current Situation

- Debug and Release builds pass with zero warnings/errors.
- All 98 tests pass in both configurations.
- Delivery capability safety, bounded false-result retry, stop cleanup/cancellation, fail-closed restart handling, protocol-path containment, and dependency-direction guards are implemented.
- `UI_DESIGN_BRIEF.md` and `CLAUDE_UI_DESIGN_PROMPT.md` are ready for Claude Design.
- The current App remains a console diagnostic host.

## Problem

Implementing WPF screens before the visual hierarchy, navigation, interaction states, accessibility behavior, Dry Run/Live treatment, and error/recovery flows are approved would create avoidable rework and could obscure safety-critical state.

## Required Changes

None until the approved design artifacts are added or summarized in the repository and Codex issues a new implementation prompt.

## Files / Components to Inspect

- `UI_DESIGN_BRIEF.md`
- `CLAUDE_UI_DESIGN_PROMPT.md`
- Future approved design artifacts

## Tests Required

None during this hold.

## Validation

Do not alter source, tests, project files, or management files.

## Constraints

- Do not begin WPF implementation from assumptions.
- Do not begin real UI Automation message delivery.
- Do not modify `CodexPrompt.md`, `PROJECT_REQUIREMENTS.md`, or `PROJECT_STATUS.md`.
- Do not commit or push.

## Definition of Done

This hold ends only after the UI design is available for technical and accessibility review and a new scoped implementation task is issued.

## Notes

The design task is external to Claude Code and is defined in `CLAUDE_UI_DESIGN_PROMPT.md`.

## Timestamp

2026-09-02T19:14:16+03:30
