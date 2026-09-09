# Headless mode — Claude CLI ↔ Codex CLI, no window

This is Agent Bridge with the desktop applications taken out of the loop
entirely. Claude and Codex each run as a command line process; nothing reads,
focuses or types into a window. The orchestration is the same code the GUI uses
— the same state machine, file watchers, hash deduplication, iteration limit and
persisted state — only the transport and the host are different.

## Why it survives a locked screen

The desktop path drives the two applications through UI Automation: it finds a
window, focuses an editor, types, and reads back a receipt. Windows tears down
that surface when the session locks, so those steps stop working — that is not a
bug in the adapters, it is what a locked desktop means.

A process has none of that. `claude --print` and `codex exec` read their
instruction from standard input and write files; whether anyone is looking at
the screen never enters into it. The headless host does not even reference the
UI Automation assembly, which an architecture test enforces.

**Locking the screen is fine. Signing out is not** — signing out ends your
processes. Leave the session logged in and locked.

## Prerequisites

```bash
npm install -g @anthropic-ai/claude-code @openai/codex
```

Then sign in to each one once, interactively (`claude`, `codex`). The headless
host runs as you and uses those stored sessions; it cannot answer a login
prompt.

## Running it

```bash
scripts\agent-bridge.bat --project "E:\YourRepo" --status
```

`--status` prints the resolved setup and where each executable was found, and
changes nothing. When that looks right:

```bash
scripts\agent-bridge.bat --project "E:\YourRepo" --start-with codex --live
```

Or from PowerShell directly:

```bash
.\scripts\agent-bridge.ps1 --project "E:\YourRepo" --start-with codex --live
```

The first run publishes the host into `artifacts\headless`; later runs reuse it.
Pass `-Rebuild` to the PowerShell script after changing the source.

Every option is written back to the settings file, so a later run with no
options repeats the last one. Stop a run with Ctrl+C — an agent that is mid-run
is stopped with it.

## The loop

1. `--start-with codex` means the bridge waits for `CodexPrompt.md`. If that file
   already has content when you start, that content starts the loop immediately.
2. The prompt is wrapped in the Claude instruction template and written to
   `claude --print` on standard input, running in your project directory.
3. Claude implements, then rewrites `ClaudeResultReport.md`. The bridge sees the
   file change but holds it until the `claude` process actually exits, so a
   half-written report is never acted on.
4. The report is handed to `codex exec` the same way. Codex reviews the real
   repository state and rewrites `CodexPrompt.md`.
5. Back to step 2, until `--max-iterations` is reached or you stop it.

`--start-with claude` starts from the other side, waiting for a report first.

## Options that matter

| Option | Why you would change it |
|---|---|
| `--live` | The default is a dry run that logs what it *would* send. Nothing reaches an agent until you pass this. |
| `--max-iterations <n>` | The loop is otherwise capped at 50. Start with a small number. |
| `--claude-args "<args>"` | Default `--print --permission-mode acceptEdits`. Claude accepts file edits without asking but still asks before some commands, and in an unattended run there is nobody to ask. If the loop needs to build and test on its own, use `--print --dangerously-skip-permissions` — and understand you are handing an unsupervised agent your repository. |
| `--codex-args "<args>"` | Default `exec --sandbox workspace-write --skip-git-repo-check -`. The trailing `-` is what makes Codex read the prompt from standard input; keep it. |
| `--claude-timeout` / `--codex-timeout` | How long one run may take before it is abandoned (3600s / 1800s). A run killed on timeout produces no report and the loop waits. |
| `--settings <path>` | Use a settings file of your own. State is kept beside it, so two projects with two settings files do not resume each other's iteration count. |
| `--reset-state` | Start fresh instead of resuming. |

## Watching a run

There is no window, by design — a console window belongs to a desktop session,
which is the thing this mode exists to not depend on. What you watch instead:

```bash
scripts\watch-bridge.ps1
```

That follows today's log and renders it: bridge events as they are, agent output
condensed to one readable line per event — Claude's text, its tool calls, its
result, and any API retries. Pass `-Full` to see every line unchanged.

The raw log is there too, if you would rather read it yourself:

```bash
Get-Content "$env:LOCALAPPDATA\AgentBridge\cli\logs\$((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')).log" -Wait -Tail 20
```

Each line an agent prints is logged as it arrives, prefixed with the agent name
(`Claude CLI > ...`). A run that prints thousands of lines stops being logged
line by line after 2000 of them and is kept for diagnostics instead, so one
chatty run cannot fill the day's log.

How much you see depends on what the agent prints:

- **Codex** streams its steps as it works, so its progress is visible live.
- **Claude** with the default `--print` buffers everything and prints once at the
  end. For live progress add
  `--claude-args "--print --verbose --output-format stream-json"` — then each
  step arrives as a JSON line as it happens.

To check whether an agent is running at all:

```bash
Get-CimInstance Win32_Process -Filter "Name='claude.exe' OR Name='codex.exe'" | Select-Object ProcessId, Name, CreationDate
```

## When an agent runs out of allowance

An exhausted allowance is a delay, not a fault, and the run treats it as one.
When an agent refuses because it has nothing left to spend, the bridge:

1. says so — a warning in the log and a notification, naming the agent and when
   it expects to work again;
2. waits, without ending the run or advancing the iteration;
3. resends the same instruction once the allowance is back.

It gives up after five refusals in a row on one iteration, so an account that is
genuinely finished stops rather than resending all night. The wait uses the reset
time the agent itself announced; when it announces none, the bridge waits twenty
minutes and tries again.

## When an agent produces nothing

An agent can exit cleanly having written no protocol file — usually because it
needed a permission nobody was there to grant. Both of this bridge's waits are
passive, so nothing would end that wait: a real run once sat in it for eight
hours. The bridge now notices that its agent stopped without writing, and ends
the run with an error naming the agent and the file, rather than waiting for an
event that is not coming.

If that happens, read the agent's own output in the log. The usual fix is
`--claude-args "--print --dangerously-skip-permissions"`.

## Where things are

- Settings and state: `%LOCALAPPDATA%\AgentBridge\cli\`, unless `--settings` says otherwise
- Logs: `%LOCALAPPDATA%\AgentBridge\cli\logs\` — one file per UTC day
- Published host: `artifacts\headless\agent-bridge.exe`

Exit codes: `0` finished, `1` ended in error, `2` bad arguments, `3` an agent
was not found on PATH.

## The desktop app

Everything above is also available with a window: install
`artifacts\installer\AgentBridge-<version>.msi` (build it with
`installer\build-msi.ps1`), pick the project folder, and press Start. The app
shows live status, the activity log including each agent's output, and raises a
Windows notification for the states worth interrupting you for — an exhausted
allowance among them.

It drives both agents through their command lines by default, for the same
reason this document exists. The desktop-window route is still there in
Settings for anyone who wants to watch the conversation happen inside the apps,
and it is the one that stops working when the screen locks.

The MSI carries the headless host too, so an installed machine can run the loop
from a scheduled task without a second download.

## Before you leave it running

Dry-run it first. Then run it live with `--max-iterations 1` and read what both
agents actually did. This mode gives two agents unsupervised write access to a
repository; a Git branch you can throw away is the right place to point it.
