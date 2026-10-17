#!/usr/bin/env bash
# Spike S2 (ADR 0007): run the determinism harness on every runtime/arch/configuration available on this machine
# and compare the hash streams. macOS arm64: x64 runs go through Rosetta 2 with an x64 .NET installed separately:
#   dotnet-install.sh --architecture x64 --runtime dotnet --channel 8.0  --install-dir ~/.dotnet-x64 --no-path
#   dotnet-install.sh --architecture x64 --runtime dotnet --channel 10.0 --install-dir ~/.dotnet-x64 --no-path
# Environment: DOTNET (arm64/native muxer, default ~/.dotnet/dotnet), DOTNET_X64 (default ~/.dotnet-x64/dotnet),
# AOT=0 to skip the NativeAOT builds, TICKS (default 2000).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
proj="$here/Tzar2.Spikes.S2/Tzar2.Spikes.S2.csproj"
bin="$here/Tzar2.Spikes.S2/bin"
out="$here/out"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
dotnet_x64="${DOTNET_X64:-$HOME/.dotnet-x64/dotnet}"
ticks="${TICKS:-2000}"
export DOTNET_ROOT="$(dirname "$dotnet")"

rm -rf "$out"
mkdir -p "$out"

for cfg in Debug Release; do
  "$dotnet" build "$proj" -c "$cfg" -v quiet -nologo
done

run() { # label, then the command
  local label="$1"; shift
  echo "== $label"
  "$@" --ticks "$ticks" --out "$out" --label "$label" | grep -E '^(final|lua|sim_ms)'
}

native_arch="$(uname -m)"
for tfm in net8.0 net10.0; do
  for cfg in Debug Release; do
    dll="$bin/$cfg/$tfm/Tzar2.Spikes.S2.dll"
    v="${tfm#net}"; v="${v%.0}"
    run "$native_arch-net$v-$(echo "$cfg" | tr '[:upper:]' '[:lower:]')" "$dotnet" "$dll"
    if [[ "$(uname -s)" == "Darwin" && "$native_arch" == "arm64" && -x "$dotnet_x64" ]]; then
      run "x64rosetta-net$v-$(echo "$cfg" | tr '[:upper:]' '[:lower:]')" arch -x86_64 "$dotnet_x64" "$dll"
    fi
  done
done

# A second process of the same configuration: anything that differs here is per-process (hash codes), not per-platform.
run "$native_arch-net10-release-run2" "$dotnet" "$bin/Release/net10.0/Tzar2.Spikes.S2.dll"

if [[ "${AOT:-1}" == "1" && "$(uname -s)" == "Darwin" ]]; then
  for rid in osx-arm64 osx-x64; do
    pub="$here/Tzar2.Spikes.S2/bin/aot-$rid"
    if "$dotnet" publish "$proj" -c Release -f net10.0 -r "$rid" -p:Tzar2Aot=true -o "$pub" -v quiet -nologo; then
      if [[ "$rid" == "osx-x64" ]]; then
        run "x64rosetta-net10-aot" arch -x86_64 "$pub/Tzar2.Spikes.S2"
      else
        run "arm64-net10-aot" "$pub/Tzar2.Spikes.S2"
      fi
    else
      echo "!! NativeAOT publish for $rid failed"
    fi
  done
fi

python3 -I "$here/compare.py" "$out" | tee "$out/comparison.md"
