<#
.SYNOPSIS
  Tzar2 capture session: measures the original TZAR on this PC and writes ONE zip to send back (ADR 0030).

.DESCRIPTION
  1. checks Windows, the .NET SDK and the game folder;
  2. builds the t2 tool from this repository;
  3. checks the game against the GOG v1.10 English reference (t2 verify-original);
  4. runs a self-test of screen capture, input and memory reading (t2 fidelity selftest);
  5. runs the capture scenarios (t2 fidelity capture): the game starts and stops by itself several times;
  6. prints the path of the result zip.

  Your game folder is never modified: the session works on a private copy in %LOCALAPPDATA%\Tzar2Capture.
  Do not touch the mouse or keyboard while the game windows come and go (about 25 minutes).
  Ctrl+C in this window stops the session safely; the zip then contains what was captured so far.

.EXAMPLE
  pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar"

.EXAMPLE
  pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Scenarios smoke,terrain-tiles

.EXAMPLE
  pwsh tools/capture/run-capture.ps1 -TzarPath "C:\GOG Games\Tzar" -Batch B
#>
[CmdletBinding()]
param(
    # The game folder (the one that contains Tzar.exe).
    [Parameter(Mandatory = $true)][string]$TzarPath,
    # Scenario ids (see: t2 fidelity scenarios). Default: the default set. "all" runs every scenario.
    [string[]]$Scenarios = @(),
    # A prepared batch of at most 30 minutes (ADR 0112): A = cheats, colours, combat, shadows, production;
    # B = unit speeds (first half) and gathering; C = unit speeds (second half); D = the day clock; E-I: ADR 0113;
    # J = repair per blow and the spy's disguise (the owner's questions, 2026-10-21).
    # Overrides -Scenarios.
    [ValidateSet("A", "B", "C", "D", "E", "F", "G", "H", "I", "J")][string]$Batch = "",
    # Where the session folder and the zip go.
    [string]$Out = (Join-Path ([Environment]::GetFolderPath("UserProfile")) "Tzar2Capture"),
    # cnc-ddraw (default: a pinned, verified DirectDraw wrapper that runs the game in a window),
    # existing (whatever wrapper the game folder already has) or native (system DirectDraw, full screen).
    [ValidateSet("cnc-ddraw", "existing", "native")][string]$Renderer = "cnc-ddraw",
    # Capture even if the game files differ from the reference build (the results are marked as such).
    [switch]$AllowModified,
    # Skip the self-test (only if it fails for a known, harmless reason).
    [switch]$SkipSelfTest
)

$ErrorActionPreference = "Stop"
$batches = @{
    "A" = @("smoke", "player-colours", "attack-timing", "shadow-overlap", "train-time", "build-time")
    "B" = @("unit-speed", "gather-wood", "gather-rock-a", "gather-rock-b", "gather-cow")
    "C" = @("unit-speed-more")
    "D" = @("day-clock")
    # ADR 0113 (after batch A's cheat findings): E = combat details, research, gathering, capture; F = fire, repair,
    # healing, field decay, experience; G = magic, loans, score; H = combat outcomes; I = trade and gambling.
    "E" = @("smoke", "group-speed", "research-feudalism", "damage-bonuses", "damage-building", "gather-wood-far", "capture-time-1", "capture-time-3", "build-card")
    "F" = @("smoke", "fire-repair-10s", "fire-repair-20s", "fire-repair-30s", "healing", "field-decay", "experience")
    "G" = @("smoke", "mana-spell", "loans", "score")
    "H" = @("smoke", "combat-outcomes")
    "I" = @("smoke", "trade", "gamble")
    # J (2026-10-21): hit points per repair blow on three buildings; how close another kingdom's spy unmasks a disguise,
    # and the disguise meter.
    "J" = @("smoke", "repair-mill", "repair-barracks", "repair-castle", "spy-unmask", "spy-mastery")
}
if ($Batch) { $Scenarios = $batches[$Batch] }
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
$started = Get-Date

function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host ""; Write-Host "STOPPED: $text" -ForegroundColor Red; exit 1 }

Write-Host "Tzar2 capture session (about 25 minutes, most of it hands-off)." -ForegroundColor Green

Step "1/6 Checking the computer"
if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) { Fail "this script must run on Windows (the original game runs there)." }
if (-not [Environment]::Is64BitOperatingSystem) { Fail "a 64-bit Windows is required." }
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail "the .NET SDK is not installed. Install it with:  winget install Microsoft.DotNet.SDK.10   then open a new terminal and rerun." }
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) { Fail ".NET SDK 10 is required (found: $($sdks -join '; ')). Install it with:  winget install Microsoft.DotNet.SDK.10" }
Write-Host "    .NET SDK: $(($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1))"
$TzarPath = (Resolve-Path $TzarPath -ErrorAction SilentlyContinue)
if (-not $TzarPath -or -not (Test-Path (Join-Path $TzarPath "Tzar.exe"))) { Fail "no Tzar.exe in the folder given with -TzarPath." }
Write-Host "    Game folder: $TzarPath"
$running = Get-Process -Name "Tzar", "TzarEdit" -ErrorAction SilentlyContinue
if ($running) { Fail "Tzar is running. Close the game (and the editor) and rerun." }

Step "2/6 Building the t2 tool (first time: about a minute)"
Push-Location $repo
try {
    & dotnet build src/Tzar2.Tools.Cli/Tzar2.Tools.Cli.csproj -c Release -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Fail "the build failed (see the messages above)." }
} finally { Pop-Location }
$t2 = Join-Path $repo "src/Tzar2.Tools.Cli/bin/Release/net10.0/t2.exe"
if (-not (Test-Path $t2)) { Fail "t2.exe was not built at $t2." }
$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = "unknown" }
Write-Host "    t2 built from commit $commit"

Step "3/6 Checking the game files against the GOG v1.10 English reference"
& $t2 verify-original $TzarPath --quiet
$verify = $LASTEXITCODE
if ($verify -eq 1 -and -not $AllowModified) {
    Fail "the game files differ from the reference build. Reinstall the GOG version, or rerun with -AllowModified to capture anyway."
} elseif ($verify -eq 1) {
    Write-Host "    Continuing with a modified install (-AllowModified); the results will say so." -ForegroundColor Yellow
} elseif ($verify -ne 0) { Fail "could not check the game folder." }

Step "4/6 Self-test: screen capture, keyboard/mouse input, memory reading (a small coloured window flashes)"
if ($SkipSelfTest) {
    Write-Host "    skipped (-SkipSelfTest)" -ForegroundColor Yellow
} else {
    & $t2 fidelity selftest
    if ($LASTEXITCODE -ne 0) {
        Fail "the self-test failed. Make sure this terminal is the active window and nothing else grabs the keyboard, then rerun. If it keeps failing, send us the output above."
    }
}

Step "5/6 Capturing (the game will start and stop several times; please do not touch the mouse or keyboard)"
$captureArgs = @("fidelity", "capture", "--tzar", "$TzarPath", "--out", $Out, "--renderer", $Renderer, "--commit", $commit,
    "--capture-dir", (Join-Path $repo "tools/capture"))
if ($Scenarios.Count -gt 0) { $captureArgs += @("--scenarios", ($Scenarios -join ",")) }
if ($AllowModified) { $captureArgs += "--allow-modified" }
& $t2 @captureArgs
$capture = $LASTEXITCODE

Step "6/6 Done"
$zip = Get-ChildItem -Path $Out -Filter "capture-*.zip" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started } |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $zip) { Fail "no result zip was written (exit code $capture). Please send us the console output." }
$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
Write-Host ""
Write-Host "Result: $($zip.FullName)  ($([math]::Round($zip.Length / 1MB)) MB, $minutes minutes)" -ForegroundColor Green
Write-Host "Please send this ONE file back (Google Drive / Telegram / any file share)."
if ($capture -ne 0) { Write-Host "Note: some captures look unusable; send the zip anyway, its log.txt explains what happened." -ForegroundColor Yellow }
try { Start-Process explorer.exe -ArgumentList "/select,`"$($zip.FullName)`"" } catch { }
exit 0
