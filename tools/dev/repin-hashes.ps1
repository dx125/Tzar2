<#
.SYNOPSIS
    Re-pins every pinned hash of the repository in one run: the determinism goldens, the trigger snapshot's golden and
    the three CI bench hashes in .github/workflows/ci.yml.

.DESCRIPTION
    Pinned hashes change on purpose when the rules or a gameplay system change (DEVELOPMENT.md). This script runs the
    tests that pin them, takes each "Expected / Actual" pair from the output and writes the new value in place, until
    the tests pass; then it runs the CI's bench commands with exactly the arguments in ci.yml and writes their final
    hashes. It prints every value it changed, for the commit message. Run it after `dotnet build Tzar2.sln -c Release`
    passes; it never re-pins a hash that differs between runs (that is a determinism bug, not a re-pin).

.EXAMPLE
    pwsh tools/dev/repin-hashes.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
)

$ErrorActionPreference = 'Stop'
Set-Location $Root

function Invoke-Tests([string]$Project, [string]$Filter) {
    & dotnet build $Project -c Release -v q | Out-Null
    $arguments = @('test', $Project, '-c', 'Release', '--no-build', '-f', 'net8.0')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    return (& dotnet @arguments 2>&1 | Out-String)
}

function Set-Pinned([string[]]$Files, [string]$Old, [string]$New) {
    foreach ($file in $Files) {
        $text = [IO.File]::ReadAllText($file)
        if ($text.Contains($Old)) {
            [IO.File]::WriteAllText($file, $text.Replace($Old, $New))
            Write-Host "$(Split-Path $file -Leaf): $Old -> $New"
            return $true
        }
    }
    return $false
}

# 1. The determinism goldens: 0x…UL constants, printed as decimal Expected/Actual pairs.
$goldens = Get-ChildItem (Join-Path $Root 'tests/Tzar2.Determinism.Tests') -Filter '*.cs' | ForEach-Object FullName
for ($round = 0; $round -lt 8; $round++) {
    $out = Invoke-Tests 'tests/Tzar2.Determinism.Tests'
    $pairs = [ordered]@{}
    foreach ($m in [regex]::Matches($out, 'Expected: (\d+)\s+Actual:\s+(\d+)')) {
        $pairs[$m.Groups[1].Value] = $m.Groups[2].Value
    }

    if ($pairs.Count -eq 0) {
        if ($out -notmatch 'Passed!') { Write-Error "The determinism tests fail for another reason:`n$out" }
        Write-Host 'determinism goldens: pinned'
        break
    }

    foreach ($expected in $pairs.Keys) {
        $old = '0x{0:X16}UL' -f [UInt64]::Parse($expected)
        $new = '0x{0:X16}UL' -f [UInt64]::Parse($pairs[$expected])
        if (-not (Set-Pinned $goldens $old $new)) { Write-Error "Pinned value $old not found." }
    }
}

# 2. The trigger snapshot's golden: a hex string.
$triggers = Get-ChildItem (Join-Path $Root 'tests/Tzar2.Scripting.Tests') -Filter '*.cs' | ForEach-Object FullName
$out = Invoke-Tests 'tests/Tzar2.Scripting.Tests' 'FullyQualifiedName~TriggerSnapshotTests'
foreach ($m in [regex]::Matches($out, 'Expected: "([0-9A-F]{16})"\s+Actual:\s+"([0-9A-F]{16})"')) {
    [void](Set-Pinned $triggers $m.Groups[1].Value $m.Groups[2].Value)
}
Write-Host 'trigger snapshot: pinned'

# 3. The CI benches: each "bench …" step of ci.yml, run with its own arguments, its --expect-hash replaced.
$ci = Join-Path $Root '.github/workflows/ci.yml'
$yaml = [IO.File]::ReadAllText($ci)
& dotnet build src/Tzar2.Tools.Cli -c Release -v q | Out-Null
foreach ($m in [regex]::Matches($yaml, '\n\s+(bench [^\n]*(?:\n\s+--[^\n]*)*?)\n\s+--expect-hash ([0-9A-F]{16})')) {
    $arguments = ($m.Groups[1].Value -split '\s+' | Where-Object { $_ }) | Select-Object -Skip 1
    $old = $m.Groups[2].Value
    $out = & dotnet run -c Release --no-build --project src/Tzar2.Tools.Cli -- bench @arguments 2>&1 | Out-String
    if ($out -notmatch 'final hash ([0-9A-F]{16})') { Write-Error "No final hash from bench $arguments`n$out" }
    $new = $Matches[1]
    $scenario = if ($arguments -contains '--scenario') { $arguments[[array]::IndexOf($arguments, '--scenario') + 1] } else { 'sim' }
    if ($new -ne $old) {
        $yaml = $yaml.Replace("--expect-hash $old", "--expect-hash $new")
        Write-Host "bench $($scenario): $old -> $new"
    }
    else {
        Write-Host "bench $($scenario): unchanged"
    }
}
[IO.File]::WriteAllText($ci, $yaml)
