<#
.SYNOPSIS
  Plays Tzar2 from the repository: imports the original game into a package when needed, builds the client and starts
  it in a window (Windows PowerShell 5.1 or PowerShell 7).

.DESCRIPTION
  1. The package: -Package, or <PlayDir>\base.original.t2pkg, imported from -TzarPath with `t2 import` when it is
     missing or older than the importer (pass -Reimport to force it). The package holds assets converted from your
     own copy of the game; it stays outside the repository.
  2. Builds the Godot client (Debug, the configuration the Godot editor binary loads).
  3. Starts Godot 4.7 .NET on src/Tzar2.Client with the package, windowed, and waits until the game is closed.
  4. Prints where the run's log folder and the match replays are (ADR 0176): send both after a play-test.

.EXAMPLE
  pwsh tools/play/play.ps1 -TzarPath "E:\Games\Tzar" -Godot "D:\tools\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe"
#>
param(
    # The original game (GOG v1.10); only read, to import the package.
    [string]$TzarPath = $env:TZAR2_ORIGIN,
    # Godot 4.7.2 .NET editor binary (not the console one).
    [string]$Godot = $env:TZAR2_GODOT,
    # Where the imported package lives.
    [string]$PlayDir = (Join-Path $env:LOCALAPPDATA "Tzar2Play"),
    # A package to play instead of <PlayDir>\base.original.t2pkg.
    [string]$Package,
    # A General MIDI SoundFont (.sf2) for the music (optional; sounds play without it).
    [string]$SoundFont = $env:TZAR2_SOUNDFONT,
    # Import again even if the package exists.
    [switch]$Reimport,
    # Extra client arguments, e.g. --skirmish or --campaign (see `--help`).
    [string[]]$ClientArgs = @()
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo

function Fail([string]$message) {
    Write-Host "STOPPED: $message" -ForegroundColor Red
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "dotnet was not found. Install the .NET SDK 10.0.402 and the .NET 8 runtime, or set DOTNET_ROOT and PATH to a portable one."
}

if (-not $Godot -or -not (Test-Path $Godot)) {
    Fail "Godot 4.7.2 .NET was not found. Pass -Godot <path to Godot_v4.7.2-stable_mono_win64.exe> or set TZAR2_GODOT."
}

if ($Godot -match "_console\.exe$") {
    $windowed = $Godot -replace "_console\.exe$", ".exe"
    if (Test-Path $windowed) { $Godot = $windowed }
}

# 1. The package.
if (-not $Package) {
    $Package = Join-Path $PlayDir "base.original.t2pkg"
    $importer = Join-Path $repo "src/Tzar2.Import"
    $stale = (Test-Path $Package) -and ((Get-ChildItem $importer -Recurse -Filter *.cs | Measure-Object LastWriteTime -Maximum).Maximum -gt (Get-Item $Package).LastWriteTime)
    if ($Reimport -or -not (Test-Path $Package) -or $stale) {
        if (-not $TzarPath -or -not (Test-Path $TzarPath)) {
            Fail "No package at $Package and no original game to import it from: pass -TzarPath <your Tzar install> (or set TZAR2_ORIGIN)."
        }

        Write-Host "Importing $TzarPath into $PlayDir ..."
        New-Item -ItemType Directory -Force $PlayDir | Out-Null
        & dotnet run -c Release --project src/Tzar2.Tools.Cli -- import $TzarPath $PlayDir --quiet
        if ($LASTEXITCODE -ne 0) { Fail "the import failed (exit $LASTEXITCODE); see $PlayDir\import-report.txt" }
    }
}

if (-not (Test-Path $Package)) { Fail "no package at $Package" }

# 2. The client.
Write-Host "Building the client ..."
& dotnet build src/Tzar2.Client/Tzar2.Client.csproj -c Debug -nologo -v q
if ($LASTEXITCODE -ne 0) { Fail "building the client failed (exit $LASTEXITCODE)" }

# 3. Play.
$arguments = @("--path", (Join-Path $repo "src/Tzar2.Client"), "--", "--package", $Package)
if ($SoundFont -and (Test-Path $SoundFont)) { $arguments += @("--soundfont", $SoundFont) }
$arguments += $ClientArgs
Write-Host "Starting Tzar2 (close the window to finish) ..."
$started = Get-Date
$process = Start-Process -FilePath $Godot -ArgumentList ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -PassThru -Wait

# 4. Where the evidence is.
$user = Join-Path $env:APPDATA "Godot\app_userdata\Tzar2"
$run = Get-ChildItem (Join-Path $user "logs") -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-5) } | Sort-Object Name -Descending | Select-Object -First 1
$replays = Get-ChildItem (Join-Path $user "replays") -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $started }
Write-Host ""
Write-Host "Tzar2 exited with code $($process.ExitCode)."
if ($run) { Write-Host "This run's log folder: $($run.FullName)" } else { Write-Host "No log folder of this run under $user\logs." }
if ($replays) { $replays | ForEach-Object { Write-Host "Replay: $($_.FullName)" } } else { Write-Host "No replay saved in this run." }
