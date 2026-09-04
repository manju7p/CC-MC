<#
.SYNOPSIS
  Uninstalls the CC-MC Local Device Gateway Windows service.

.DESCRIPTION
  Stops the service and deregisters it from the SCM via WinSW. Run as
  Administrator, from the same deployment directory install.ps1 was run
  from.

  Deliberately does NOT delete data\, logs\, or config\ - per
  docs/gateway-decision.md §8, local data (SQLite DB, logs, config) must
  survive an uninstall so it never silently destroys un-synced offline
  transactions sitting in the outbox (Checkpoint 3+). A human who wants
  those gone has to remove them explicitly.

  NOT VERIFIED ON A REAL WINDOWS MACHINE - see the same note in install.ps1
  and docs/gateway-decision.md §10.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $installDir "CCMCGateway.exe"

if (-not (Test-Path $exePath)) {
    Fail "WinSW executable not found at $exePath - nothing to uninstall from this directory."
}

Write-Host "Stopping CC-MC Gateway service (if running)..."
& $exePath stop
# Non-fatal if it wasn't running - proceed to uninstall regardless.

Write-Host "Deregistering service from Windows Service Control Manager..."
& $exePath uninstall
if ($LASTEXITCODE -ne 0) {
    Fail "CCMCGateway.exe uninstall failed with exit code $LASTEXITCODE."
}

Write-Host "CC-MC Gateway service uninstalled."
Write-Host "Local data, config, and logs were left in place:"
Write-Host "  data\   (SQLite DB, once implemented)"
Write-Host "  config\ (gateway.config.json)"
Write-Host "  logs\   (WinSW and application logs)"
Write-Host "Remove them manually if you want a fully clean machine."
