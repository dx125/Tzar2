# Downloads the GeneralUser GS SoundFont (GeneralUser GS License v2.0, see tools/soundfont/README.md) into the
# git-ignored soundfonts/ folder for MIDI playback in the client during development (ADR 0020). Never committed or bundled.
$ErrorActionPreference = 'Stop'

$commit = '684543d5e5efaef08d02be50dcda8d552478fa60' # GeneralUser GS v2.0.3 (mrbumpy409/GeneralUser-GS)
$sha256 = '9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe'
$base = "https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/$commit"

$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$out = Join-Path $root 'soundfonts'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$target = Join-Path $out 'GeneralUser-GS.sf2'

if ((Test-Path $target) -and ((Get-FileHash -Algorithm SHA256 $target).Hash.ToLowerInvariant() -eq $sha256)) {
    Write-Host 'soundfonts/GeneralUser-GS.sf2 is already present and verified.'
    exit 0
}

$part = "$target.part"
Invoke-WebRequest "$base/GeneralUser-GS.sf2" -OutFile $part
Invoke-WebRequest "$base/documentation/LICENSE.txt" -OutFile (Join-Path $out 'GeneralUser-GS-LICENSE.txt')
$actual = (Get-FileHash -Algorithm SHA256 $part).Hash.ToLowerInvariant()
if ($actual -ne $sha256) {
    Remove-Item $part
    throw "SHA-256 mismatch: expected $sha256, got $actual"
}

Move-Item -Force $part $target
Write-Host 'Saved soundfonts/GeneralUser-GS.sf2 (license: soundfonts/GeneralUser-GS-LICENSE.txt).'
