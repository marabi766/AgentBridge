<#
.SYNOPSIS
    Builds the Agent Bridge MSI.

.DESCRIPTION
    Publishes the desktop app self-contained for win-x64, then packages that
    output with WiX. Self-contained because the machines this installs on are
    administered remotely and left alone, where a missing .NET runtime would be
    discovered only when someone tried to start the bridge.

    The headless host is published alongside it, so an installed machine can run
    the loop from a scheduled task or a shell without a second download.

    Needs the WiX tool once per machine:
        dotnet tool install --global wix

.PARAMETER Version
    Product version stamped into the MSI. Must be numeric (a.b.c.d); WiX rejects
    anything else.

.PARAMETER Output
    Where to write the .msi. Defaults to artifacts\installer.

.EXAMPLE
    .\build-msi.ps1 -Version 1.2.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = '1.2.0.0',
    [string]$Output
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "Version must be numeric like 1.2.0.0, not '$Version'."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot 'artifacts\installer-payload'
if (-not $Output) { $Output = Join-Path $repositoryRoot 'artifacts\installer' }
$msi = Join-Path $Output "AgentBridge-$Version.msi"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "The WiX tool is not installed. Run: dotnet tool install --global wix"
}

# A stale payload would be packaged as happily as a fresh one, and the resulting
# MSI would look correct while installing the previous build.
if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}

Write-Host 'Publishing the desktop app (self-contained, win-x64)...' -ForegroundColor Cyan
dotnet publish (Join-Path $repositoryRoot 'src\AgentBridge.App\AgentBridge.App.csproj') `
    -c Release -r win-x64 --self-contained true -o $publishDirectory `
    -p:Version=$Version -p:FileVersion=$Version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed with exit code $LASTEXITCODE." }

Write-Host 'Publishing the headless host alongside it...' -ForegroundColor Cyan
dotnet publish (Join-Path $repositoryRoot 'src\AgentBridge.Cli\AgentBridge.Cli.csproj') `
    -c Release -r win-x64 --self-contained true -o $publishDirectory `
    -p:Version=$Version -p:FileVersion=$Version | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Publishing the headless host failed with exit code $LASTEXITCODE." }

New-Item -ItemType Directory -Force $Output | Out-Null

Write-Host 'Packaging...' -ForegroundColor Cyan
wix build (Join-Path $PSScriptRoot 'AgentBridge.wxs') `
    -d "PublishDir=$publishDirectory" `
    -d "BuildVersion=$Version" `
    -arch x64 `
    -o $msi | Out-Host
if ($LASTEXITCODE -ne 0) { throw "WiX failed with exit code $LASTEXITCODE." }

$size = [Math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "Built $msi ($size MB)" -ForegroundColor Green
