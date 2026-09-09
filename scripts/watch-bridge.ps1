<#
.SYNOPSIS
    Follows a headless Agent Bridge run and prints what the agents are doing.

.DESCRIPTION
    Tails today's bridge log. Bridge events are shown as they are; agent output
    is condensed to one readable line per event, because an agent asked for
    machine-readable output emits JSON that nobody wants to read raw.

    Codex prints prose and is shown verbatim. Claude, when run with
    --output-format stream-json, emits one JSON object per event: this renders
    its text, its tool calls and its final result, and drops the token-counting
    chatter in between.

.PARAMETER Full
    Print every line unchanged, including the raw JSON. Use when you need to see
    something this view drops.

.PARAMETER LogsDirectory
    Where to look for the log. Defaults to the headless host's own directory.

.EXAMPLE
    .\watch-bridge.ps1
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [string]$LogsDirectory = (Join-Path $env:LOCALAPPDATA 'AgentBridge\cli\logs')
)

$ErrorActionPreference = 'Stop'

# The log is named for the UTC day, which is not necessarily today's local date.
$log = Join-Path $LogsDirectory ((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd') + '.log')
if (-not (Test-Path $log)) {
    Write-Host "No log for today at $log — is a run in progress?" -ForegroundColor Yellow
    return
}

Write-Host "Following $log — Ctrl+C to stop watching (the run keeps going)." -ForegroundColor Cyan

function Write-AgentLine([string]$agent, [string]$icon, [string]$text, [string]$colour) {
    if ([string]::IsNullOrWhiteSpace($text)) { return }
    $flat = ($text -replace '\s+', ' ').Trim()
    if ($flat.Length -gt 300) { $flat = $flat.Substring(0, 300) + '…' }
    Write-Host "$icon [$agent] $flat" -ForegroundColor $colour
}

Get-Content $log -Wait -Tail 20 | ForEach-Object {
    $line = $_

    if ($Full) { Write-Host $line; return }

    if ($line -notmatch '(?<agent>Claude CLI|Codex CLI) > (?<payload>.*)$') {
        # A bridge event: state transitions, deliveries, errors. Always worth seeing.
        if ($line -match '\[(Warning|Error)\]') { Write-Host $line -ForegroundColor Red }
        elseif ($line -match 'AgentBridge\.') { Write-Host $line -ForegroundColor DarkGray }
        return
    }

    $agent = $matches['agent']
    $payload = $matches['payload']

    if ($payload -notmatch '^\s*\{') {
        # Plain prose, which is how Codex reports itself.
        Write-AgentLine $agent '·' $payload 'Gray'
        return
    }

    try { $event = $payload | ConvertFrom-Json -ErrorAction Stop }
    catch {
        # A line the adapter had to cut, or output that only looks like JSON.
        Write-AgentLine $agent '·' $payload 'DarkGray'
        return
    }

    switch ($event.type) {
        'assistant' {
            foreach ($block in $event.message.content) {
                switch ($block.type) {
                    'text'     { Write-AgentLine $agent '>' $block.text 'White' }
                    'tool_use' { Write-AgentLine $agent '*' "$($block.name) $($block.input.command ?? $block.input.file_path ?? $block.input.pattern)" 'Cyan' }
                }
            }
        }
        'result' {
            $cost = if ($null -ne $event.total_cost_usd) { " (`$$([Math]::Round($event.total_cost_usd, 2)))" } else { '' }
            Write-AgentLine $agent '=' "finished: $($event.subtype)$cost" 'Green'
        }
        'system' {
            # init and the token counters are noise; a retry is not.
            if ($event.subtype -eq 'api_retry') {
                Write-AgentLine $agent '!' "API retry $($event.attempt)/$($event.max_retries)" 'Yellow'
            }
        }
    }
}
