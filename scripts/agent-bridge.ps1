<#
.SYNOPSIS
    Runs the Agent Bridge Claude/Codex loop with no window at all.

.DESCRIPTION
    Publishes the headless host once, then runs it with whatever arguments you
    pass through. Both agents are driven as processes (claude --print, codex
    exec), so the loop keeps working while the Windows session is locked —
    nothing here reads a window.

    Arguments are read straight from $args rather than through a param() block.
    They have to be: almost every one of them starts with "--", and PowerShell
    would otherwise try to bind "--project" as a parameter of this script and
    fail before the host ever sees it.

    -Rebuild (or --rebuild) publishes again even if a build already exists; use
    it after changing the source. Everything else is handed to the host verbatim.
    Run with --help to see what it accepts.

.EXAMPLE
    .\agent-bridge.ps1 --project "E:\MyRepo" --status

.EXAMPLE
    .\agent-bridge.ps1 --project "E:\MyRepo" --start-with codex --live --max-iterations 10
#>

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\AgentBridge.Cli\AgentBridge.Cli.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\headless'
$executable = Join-Path $publishDirectory 'agent-bridge.exe'

$rebuild = $false
$forwarded = @()
foreach ($argument in $args) {
    switch -Regex ($argument) {
        # A lone '--' is how some shells separate their own arguments from the
        # command's. It is not something the host understands, so it is dropped.
        '^--$'                { break }
        '^(-{1,2})[Rr]ebuild$' { $rebuild = $true; break }
        default               { $forwarded += $argument }
    }
}

if ($rebuild -or -not (Test-Path $executable)) {
    Write-Host 'Publishing the headless host...' -ForegroundColor Cyan
    dotnet publish $project -c Release -o $publishDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE."
    }
}

& $executable @forwarded
exit $LASTEXITCODE
