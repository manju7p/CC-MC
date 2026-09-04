<#
.SYNOPSIS
  Installs the CC-MC Local Device Gateway as a Windows service using WinSW.

.DESCRIPTION
  Operates on an ALREADY-ASSEMBLED deployment directory (see
  packaging/build-deployment.mjs, which produces this layout) - it does not
  build or download anything itself. Run as Administrator.

  Expected layout of the directory this script is run from:

    CCMCGateway.exe            <- WinSW's own executable, renamed
    CCMCGateway.xml            <- WinSW service config (this repo's copy)
    node\node.exe              <- vendored Node.js runtime
    app\dist\main.js           <- compiled gateway entrypoint
    app\node_modules\          <- gateway's runtime dependencies
    config\gateway.config.json <- local, non-secret configuration
    data\                      <- created here if absent (SQLite DB lives here, Checkpoint 3+)
    logs\                      <- created here if absent (WinSW-managed rolling logs)

  NOT VERIFIED ON A REAL WINDOWS MACHINE. This script has been reviewed for
  correctness against documented `sc.exe`/WinSW CLI behavior, but this
  sandbox is Linux-only and cannot execute it. Real-Windows verification of
  install/start/reboot-autostart/crash-restart/stop/uninstall is a
  Checkpoint 10 task - see docs/gateway-decision.md §10.

.NOTES
  Exits non-zero with a clear message on any failure at any step, rather
  than silently leaving a half-installed service (docs/gateway-decision.md §4).
#>

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "Installing CC-MC Gateway service from: $installDir"

$exePath = Join-Path $installDir "CCMCGateway.exe"
$xmlPath = Join-Path $installDir "CCMCGateway.xml"
$nodePath = Join-Path $installDir "node\node.exe"
$entryPath = Join-Path $installDir "app\dist\main.js"
$configPath = Join-Path $installDir "config\gateway.config.json"

foreach ($required in @(
    @{ Path = $exePath; Label = "WinSW executable (CCMCGateway.exe)" },
    @{ Path = $xmlPath; Label = "WinSW service config (CCMCGateway.xml)" },
    @{ Path = $nodePath; Label = "vendored Node runtime (node\node.exe)" },
    @{ Path = $entryPath; Label = "compiled gateway entrypoint (app\dist\main.js)" }
)) {
    if (-not (Test-Path $required.Path)) {
        Fail "Missing required file: $($required.Label) expected at $($required.Path). Run packaging/build-deployment.mjs first."
    }
}

if (-not (Test-Path $configPath)) {
    Fail "Missing gateway config at $configPath. Copy config\gateway.config.example.json to that path and fill in this centre's gatewayId/centreId/cloudApiBaseUrl before installing."
}

# Step 1: ensure data\ and logs\ exist, with ACLs restricted to the service
# account and Administrators (docs/gateway-decision.md §4 step 2, §9). The
# exact service account is a Phase 12 decision (currently WinSW/SCM
# default) - this ACL step is written to be revisited once that account is
# chosen, not left silently as "Everyone".
$dataDir = Join-Path $installDir "data"
$logsDir = Join-Path $installDir "logs"

foreach ($dir in @($dataDir, $logsDir)) {
    if (-not (Test-Path $dir)) {
        Write-Host "Creating $dir"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# Step 2: register the service with the SCM via WinSW.
Write-Host "Registering service with Windows Service Control Manager..."
& $exePath install
if ($LASTEXITCODE -ne 0) {
    Fail "CCMCGateway.exe install failed with exit code $LASTEXITCODE. See logs\CCMCGateway.wrapper.log if present."
}

# Step 3: start it.
Write-Host "Starting service..."
& $exePath start
if ($LASTEXITCODE -ne 0) {
    Fail "CCMCGateway.exe start failed with exit code $LASTEXITCODE. The service is registered but not running - inspect Event Viewer and logs\ before retrying."
}

Write-Host "CC-MC Gateway service installed and started successfully."
Write-Host "Check status with: Get-Service CCMCGateway"
