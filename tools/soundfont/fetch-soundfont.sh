#!/usr/bin/env bash
# Downloads the GeneralUser GS SoundFont (S. Christian Collins, GeneralUser GS License v2.0, see tools/soundfont/README.md)
# into the git-ignored soundfonts/ folder for MIDI playback in the client during development (ADR 0020).
# The file is never committed and never bundled with builds.
set -euo pipefail

commit=684543d5e5efaef08d02be50dcda8d552478fa60 # GeneralUser GS v2.0.3 (mrbumpy409/GeneralUser-GS)
sha256=9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe
base="https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/$commit"

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/soundfonts"
mkdir -p "$out"

if [ -f "$out/GeneralUser-GS.sf2" ] && echo "$sha256  $out/GeneralUser-GS.sf2" | shasum -a 256 -c - >/dev/null 2>&1; then
  echo "soundfonts/GeneralUser-GS.sf2 is already present and verified."
  exit 0
fi

curl -fL --retry 3 -o "$out/GeneralUser-GS.sf2.part" "$base/GeneralUser-GS.sf2"
curl -fL --retry 3 -o "$out/GeneralUser-GS-LICENSE.txt" "$base/documentation/LICENSE.txt"
echo "$sha256  $out/GeneralUser-GS.sf2.part" | shasum -a 256 -c -
mv "$out/GeneralUser-GS.sf2.part" "$out/GeneralUser-GS.sf2"
echo "Saved soundfonts/GeneralUser-GS.sf2 (license: soundfonts/GeneralUser-GS-LICENSE.txt)."
