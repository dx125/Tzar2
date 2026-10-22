<#
.SYNOPSIS
  Builds the Windows x64 package of Tzar2 (ADR 0241): the Godot export, then the portable zip and the installer.

.DESCRIPTION
  Stage "export": imports the Godot project headlessly and exports the "Windows Desktop" preset into
  <OutDir>\Tzar2 (Tzar2.exe, Tzar2.console.exe, Tzar2.pck, data_Tzar2.Client_windows_x86_64\).
  Stage "package": adds version.txt, the licences, the beta readme, the optional crash-report endpoint and SHA256SUMS.txt,
  then writes <OutDir>\Tzar2-<version>-win-x64-portable.zip (entries sorted, timestamps fixed to -SourceDateEpoch) and,
  with -Iscc, <OutDir>\Tzar2-<version>-win-x64-setup.exe (Inno Setup, tools/package/tzar2.iss).
  Code signing (ADR 0242) runs between the stages and after the installer, in the workflow: tools/package/sign.ps1.
  Nothing is published: the files stay in <OutDir>.

.EXAMPLE
  pwsh tools/package/package-windows.ps1 -Stage export -Godot C:\tools\Godot_v4.7.2-stable_mono_win64_console.exe -Version 0.7.0-beta.1+local
  pwsh tools/package/package-windows.ps1 -Stage package -Version 0.7.0-beta.1+local -Iscc "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#>
param(
    [Parameter(Mandatory)][ValidateSet("export", "package")][string]$Stage,
    # The full version: <prefix>-beta.<n>+<commit> (the assemblies' informational version).
    [Parameter(Mandatory)][string]$Version,
    # Godot 4.7.2 .NET console binary with the matching export templates installed (stage export).
    [string]$Godot = $env:TZAR2_GODOT,
    [string]$OutDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "../..")) "artifacts/windows"),
    # Seconds since 1970 for the zip entries' timestamps (default: the commit's time).
    [long]$SourceDateEpoch = 0,
    # Inno Setup's compiler; without it no installer is built.
    [string]$Iscc,
    # A Sentry DSN baked in as the default crash-report endpoint (crash-reporting.dsn); empty: none.
    [string]$Dsn = ""
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
$game = Join-Path $OutDir "Tzar2"
$fileVersion = ($Version -split '\+')[0]

function Fail([string]$message) {
    Write-Host "::error::$message"
    exit 1
}

if ($Stage -eq "export") {
    if (-not $Godot -or -not (Test-Path $Godot)) { Fail "Godot was not found: pass -Godot or set TZAR2_GODOT." }
    $client = Join-Path $repo "src/Tzar2.Client"
    if (Test-Path $game) { Remove-Item $game -Recurse -Force }
    New-Item -ItemType Directory -Force $game | Out-Null

    # The version goes into every assembly through MSBuild (VersionSuffix is read from the environment).
    $env:VersionSuffix = (($fileVersion -split '-', 2) + @(""))[1]
    Write-Host "Importing the Godot project ..."
    & $Godot --headless --path $client --import
    if ($LASTEXITCODE -ne 0) { Fail "godot --import failed (exit $LASTEXITCODE)" }

    Write-Host "Exporting Windows Desktop ..."
    & $Godot --headless --path $client --export-release "Windows Desktop" (Join-Path $game "Tzar2.exe")
    if ($LASTEXITCODE -ne 0) { Fail "godot --export-release failed (exit $LASTEXITCODE)" }
    foreach ($required in @("Tzar2.exe", "Tzar2.pck")) {
        if (-not (Test-Path (Join-Path $game $required))) { Fail "the export has no $required" }
    }

    $data = Get-ChildItem $game -Directory -Filter "data_*" | Select-Object -First 1
    if (-not $data -or -not (Test-Path (Join-Path $data.FullName "Tzar2.Client.dll"))) { Fail "the export has no .NET assemblies (data_*\Tzar2.Client.dll)" }
    Get-ChildItem $game -Recurse -File | Sort-Object FullName | ForEach-Object { "{0,12}  {1}" -f $_.Length, $_.FullName.Substring($game.Length + 1) }
    exit 0
}

# Stage "package".
if (-not (Test-Path (Join-Path $game "Tzar2.exe"))) { Fail "no export in $game; run -Stage export first" }
if ($SourceDateEpoch -le 0) {
    $SourceDateEpoch = [long](git -C $repo log -1 --format=%ct)
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $game "version.txt"), "Tzar2 $Version`n", $utf8)
Copy-Item (Join-Path $repo "LICENSE") (Join-Path $game "LICENSE.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "THIRD-PARTY-NOTICES.txt") (Join-Path $game "THIRD-PARTY-NOTICES.txt") -Force
$readme = (Get-Content -Raw (Join-Path $PSScriptRoot "README-BETA.txt")).Replace("{VERSION}", $Version).Replace("`r`n", "`n")
[System.IO.File]::WriteAllText((Join-Path $game "README.txt"), $readme, $utf8)
$dsnFile = Join-Path $game "crash-reporting.dsn"
if ($Dsn) { [System.IO.File]::WriteAllText($dsnFile, "$Dsn`n", $utf8) } elseif (Test-Path $dsnFile) { Remove-Item $dsnFile }

# Checksums of every file (sorted, '/' separators), then the zip.
$files = Get-ChildItem $game -Recurse -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Name = $_.FullName.Substring($game.Length + 1).Replace('\', '/') } } |
    Sort-Object { $_.Name } -CaseSensitive
$sums = $files | ForEach-Object { "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $_.Path).Hash.ToLowerInvariant(), $_.Name }
[System.IO.File]::WriteAllText((Join-Path $game "SHA256SUMS.txt"), (($sums -join "`n") + "`n"), $utf8)
$files = @($files) + [pscustomobject]@{ Path = (Join-Path $game "SHA256SUMS.txt"); Name = "SHA256SUMS.txt" }

$zipPath = Join-Path $OutDir "Tzar2-$fileVersion-win-x64-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
Add-Type -AssemblyName System.IO.Compression
$stamp = [DateTimeOffset]::FromUnixTimeSeconds($SourceDateEpoch)
$stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($file in ($files | Sort-Object { $_.Name } -CaseSensitive)) {
        $entry = $zip.CreateEntry("Tzar2-$fileVersion/$($file.Name)", [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stamp
        $out = $entry.Open()
        try {
            $in = [System.IO.File]::OpenRead($file.Path)
            try { $in.CopyTo($out) } finally { $in.Dispose() }
        } finally { $out.Dispose() }
    }
    $zip.Dispose()
} finally { $stream.Dispose() }
Write-Host "Portable: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"

if ($Iscc) {
    if (-not (Test-Path $Iscc)) { Fail "Inno Setup compiler not found at $Iscc" }
    # Inno's numeric version: 0.7.0-beta.12 -> 0.7.0.12.
    $numeric = ($fileVersion -replace '-.*', '')
    if ($fileVersion -match '\.(\d+)$') { $numeric += ".$($Matches[1])" } else { $numeric += ".0" }
    & $Iscc "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DNumericVersion=$numeric" "/DSourceDir=$game" "/DOutputDir=$OutDir" (Join-Path $PSScriptRoot "tzar2.iss")
    if ($LASTEXITCODE -ne 0) { Fail "ISCC failed (exit $LASTEXITCODE)" }
    $setup = Join-Path $OutDir "Tzar2-$fileVersion-win-x64-setup.exe"
    if (-not (Test-Path $setup)) { Fail "ISCC wrote no $setup" }
    Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
}
