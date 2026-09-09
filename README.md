# Agent Bridge

Agent Bridge runs Claude and Codex against the same repository in a supervised
loop. Claude implements a step and writes a report; Codex reads the repository,
checks what was actually done, and writes the next instruction; Claude picks that
up. The bridge watches two files, decides when each side has genuinely finished,
and hands the work across. Nobody copies anything by hand.

It is a Windows desktop application with a headless command line host alongside
it. Both drive the same orchestration engine.

```
   ClaudeResultReport.md ──▶ Agent Bridge ──▶ Codex
                                  ▲              │
                                  │              ▼
   Claude ◀────────────────────────    CodexPrompt.md
```

## What it is for

A single agent working alone has nobody to check it. It reports success, and the
report is the only evidence. Pairing two agents makes each one's work the other's
input: Codex reviews the repository rather than the report, and its instruction
is what Claude executes next. The bridge is what makes that exchange automatic
and bounded — it counts iterations, refuses stale files, stops on failure and
survives a restart.

## Two ways to run it

**The desktop application.** A dashboard with the loop's state, an Activity page
that follows the agents' output as it happens, diagnostics, settings and a
first-run wizard. It lives in the system tray and can start minimised.

**The headless host** (`agent-bridge.exe`). No window at all. Same engine, same
state file format, driven by command line switches. Installed alongside the
application.

Both work while the Windows session is **locked**, because both drive the agents
as processes rather than through their windows. Signing out ends the run;
locking the screen does not. See [HEADLESS.md](HEADLESS.md).

## Requirements

- Windows 10 or 11, x64
- Git on `PATH`
- Claude Code and Codex, installed and signed in:

```bash
npm install -g @anthropic-ai/claude-code @openai/codex
```

Run `claude` and `codex` once each, interactively, to sign in. The bridge never
handles credentials.

The .NET runtime is **not** required — the installer ships a self-contained
build.

## Installing

Run the MSI from `artifacts/installer`. It installs to `C:\Program Files\Agent
Bridge`, adds Start menu and desktop shortcuts, and places the headless host next
to the application. Installing a newer build replaces the older one in place.

To build the installer yourself (needs the .NET 10 SDK and the WiX tool):

```bash
powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -Version 2.0.0.0
```

## Getting started

1. Open Agent Bridge. On first run it opens **Setup**: choose the project folder,
   confirm the two protocol filenames, and let it validate the Git repository.
2. In **Settings**, point Claude and Codex at their command lines and set the
   arguments (see below). Leave **Dry Run** on for the first pass.
3. On the **Dashboard**, choose the resume checkpoint — *Claude is working* or
   *Codex is working*, whichever is true right now — and press **Start**.

In Dry Run nothing is sent to either agent; the whole pipeline still runs, so you
can watch the state machine work before letting it touch anything.

## How the loop decides

The bridge does not assume an agent has finished because a file changed. It
watches for:

- **Stability.** A file must hash identically several times in a row before it
  counts as written, so a half-saved file is never acted on.
- **A genuinely new revision.** The hash of the last file it acted on is
  persisted. An unchanged file is refused, and it says so rather than waiting in
  silence.
- **Order.** A file written before the current instruction was delivered cannot
  be the answer to it.
- **The agent actually being idle.** A protocol file that arrives while the agent
  is still running is deferred until the process exits.

When an agent stops without writing its file, the run ends with an explanation
rather than waiting for ever. When it stops so quickly that it cannot have
started, the instruction is sent again — twice at most.

When an agent runs out of allowance, the bridge reads the reset time from what
the agent printed, waits, and resends itself when the allowance returns. Nothing
needs restarting.

## Configuration

Settings live in `%LOCALAPPDATA%\AgentBridge\settings.json` (the headless host
uses `%LOCALAPPDATA%\AgentBridge\cli\settings.json`). Everything below is
editable in the application's Settings page.

| Setting | What it does |
|---|---|
| Project path | The repository both agents work in |
| Protocol filenames | Defaults `ClaudeResultReport.md` and `CodexPrompt.md`; must be plain filenames in the project root |
| Claude / Codex CLI executable | Command name or full path |
| Claude / Codex CLI arguments | The exact invocation — see below |
| Claude / Codex CLI timeout | How long one run may take before it is abandoned |
| Maximum iterations | The loop stops here regardless |
| Agent log line limit | Lines of one run written to the log; `0` removes the limit |
| Dry Run | Runs the whole pipeline without sending anything |

### The arguments that matter

```
Claude:  --print --verbose --output-format stream-json --permission-mode acceptEdits
Codex:   exec --sandbox workspace-write --skip-git-repo-check -
```

`--print` makes the run non-interactive; with no prompt argument the instruction
arrives on standard input, which is what makes a long multi-line prompt safe to
pass — no quoting is involved. `--output-format stream-json` is what puts the
agent's progress in Activity as it happens rather than in one lump at the end.

The permission mode is the decision to make deliberately. `acceptEdits` lets
Claude change files but asks before running commands — and in `--print` mode
there is nobody to ask, so those commands are refused and the agent cannot run
its own tests. `--dangerously-skip-permissions` removes that gate, which is what
an unattended loop needs and is also exactly as broad as it sounds. Codex's
`--sandbox workspace-write` is the equivalent grant on its side.

Decide which you want before leaving a run unattended over a repository you care
about.

## Watching a run

The **Activity** page follows the log as it is written. Agent output is rendered
before it is stored — what the agent said and which file it touched, rather than
the JSON it was encoded in:

```
Claude CLI > [Edit] services/audit-service/src/audit/audit.repository.ts
Claude CLI > Now the status-cascade persistence gap:
Claude CLI > finished: success ($4.71)
```

**Follow** keeps it current; turn it off to scroll back without the list moving.
**Clear view** empties the list and carries on, so what appears next is only what
happens next — the log files are untouched and **Refresh** brings them back.
**Export** writes every stored day to one file.

From a terminal, without the application:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\watch-bridge.ps1
```

It finds whichever host is writing and renders the same view. Raw output for one
run is kept in **Diagnostics**.

## When something goes wrong

**Nothing is happening and the state says it is waiting.** Look at the last
action on the dashboard. If it says a protocol file is unchanged since it was
last acted on, the loop is at a dead end — the other agent only writes its file
in answer to this one. Press **Reset state**, choose the checkpoint that matches
who should be working, and Start.

**Reset state** discards the iteration counter and the recorded file hashes, so
the current files count as new again. It never touches the repository, the
settings or the logs. It is available whenever the bridge is not running.

**Retry Claude / Retry Codex** resend the current instruction without advancing
the cycle. Each is available only in the state where it makes sense: Retry Claude
while waiting for Claude's report, Retry Codex while waiting for Codex's prompt.

Logs are in `%LOCALAPPDATA%\AgentBridge\logs`, one file per UTC day.

## Headless

```bash
agent-bridge --project F:\MyRepo --status
agent-bridge --project F:\MyRepo --start-with codex --live --max-iterations 20
```

`--status` prints the resolved configuration and whether each agent is reachable,
then exits. Full documentation in [HEADLESS.md](HEADLESS.md), including running
it from Task Scheduler so a run survives a sign-out.

## Building from source

```bash
dotnet build AgentBridge.slnx -c Release
dotnet test AgentBridge.slnx -c Release
```

Needs the .NET 10 SDK. The suite is 267 tests and runs against real files, a real
`git.exe` and fake agents; no network and no installed agent CLI is required.

## Project layout

| Project | Contains |
|---|---|
| `AgentBridge.Abstractions` | Models and interfaces; depends on nothing |
| `AgentBridge.Core` | The orchestrator, state machine, retry policy, templates |
| `AgentBridge.Infrastructure` | File watching, Git, persistence, logging, the CLI adapters |
| `AgentBridge.UIAutomation` | The desktop-window adapters, used only by the app |
| `AgentBridge.Fakes` | Agent doubles used by the tests |
| `AgentBridge.App` | The WPF application |
| `AgentBridge.Cli` | The headless host |

Dependency direction is enforced by a test, not by convention. The headless host
deliberately has no reference to `AgentBridge.UIAutomation`, so the process that
must survive a locked desktop cannot pull the window-reading code in.

Design notes are in [ARCHITECTURE.md](ARCHITECTURE.md) and
[UI_AUTOMATION.md](UI_AUTOMATION.md).

## Limits worth knowing

- **Two agents, one repository, one at a time.** The bridge does not parallelise
  and does not manage branches; the agents work wherever the repository is left.
- **It cannot make an agent verify its own work.** If the permission mode
  forbids running commands, Claude will write code it cannot test and say so in
  its report. That is the setting's doing, not the bridge's.
- **The five-hour allowance is the real limit on a long run**, not the iteration
  count. The bridge waits it out rather than failing, but a night's run is paced
  by it.
- **The desktop-window adapters remain** for driving Claude Desktop and ChatGPT
  Desktop, but they cannot work on a locked session. The command line route is
  the supported one.
