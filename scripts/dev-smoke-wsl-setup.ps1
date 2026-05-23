<#
.SYNOPSIS
    Run the daily WSL gateway first-run/reset smoke loop.

.DESCRIPTION
    Thin developer wrapper over validate-wsl-gateway.ps1. The default scenario
    deletes the OpenClawGateway WSL distro between runs while preserving the
    isolated Windows tray identity, which reproduces the important reset/redo
    path without touching the user's real %APPDATA% identity.

    The underlying validator still performs the full product setup path through
    the tray UI, then requires both:
      1. setup-state.json reaches Complete, and
      2. the tray/gateway are usable afterward with stored device credentials.

.PARAMETER Scenario
    ResetRedoPreserveIdentity is the default. FreshMachine gives a single clean
    run, Recreate wipes identity every iteration, and UpstreamInstall reuses an
    existing distro if present.

.PARAMETER Iterations
    Number of setup cycles to run. ResetRedoPreserveIdentity defaults to 2 so it
    always proves "first run" plus "delete WSL and do it again".

.EXAMPLE
    .\scripts\dev-smoke-wsl-setup.ps1

.EXAMPLE
    .\scripts\dev-smoke-wsl-setup.ps1 -NoBuild

.EXAMPLE
    .\scripts\dev-smoke-wsl-setup.ps1 -Scenario FreshMachine -Iterations 1
#>

[CmdletBinding()]
param(
    [ValidateSet("FreshMachine", "ResetRedoPreserveIdentity", "Recreate", "UpstreamInstall")]
    [string]$Scenario = "ResetRedoPreserveIdentity",
    [int]$Iterations = 2,
    [string]$OutputDir = (Join-Path (Get-Location) "artifacts\wsl-gateway-validation\dev-smoke"),
    [switch]$NoBuild,
    [switch]$KeepFailedDistro,
    [switch]$ContinueOnFailure,
    [int]$TimeoutSeconds = 600,
    [int]$PostSetupUsableTimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Iterations -lt 1) {
    throw "-Iterations must be at least 1."
}

if ($Scenario -eq "ResetRedoPreserveIdentity" -and $Iterations -lt 2) {
    Write-Host "ResetRedoPreserveIdentity needs at least 2 iterations; using 2."
    $Iterations = 2
}

$validator = Join-Path $PSScriptRoot "validate-wsl-gateway.ps1"
if (-not (Test-Path -LiteralPath $validator)) {
    throw "Validator not found: $validator"
}

$validatorArgs = @{
    Scenario = $Scenario
    OutputDir = $OutputDir
    Iterations = $Iterations
    ConfirmDestructiveClean = $true
    TimeoutSeconds = $TimeoutSeconds
    PostSetupUsableTimeoutSeconds = $PostSetupUsableTimeoutSeconds
    RequireRealGatewayBootstrap = $true
    RequireOperatorPairing = $true
    RequireWindowsNodePairing = $true
}

if ($NoBuild) { $validatorArgs.NoBuild = $true }
if ($KeepFailedDistro) { $validatorArgs.KeepFailedDistro = $true }
if ($ContinueOnFailure) { $validatorArgs.ContinueOnFailure = $true }

Write-Host ""
Write-Host "============================================================"
Write-Host "     OpenClaw WSL Setup Smoke Loop"
Write-Host "============================================================"
Write-Host "  Scenario   : $Scenario"
Write-Host "  Iterations : $Iterations"
Write-Host "  OutputDir  : $OutputDir"
Write-Host ""

& $validator @validatorArgs
if (-not $?) { exit 1 }
exit $LASTEXITCODE
