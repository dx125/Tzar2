#!/usr/bin/env bash
# Spike S1 benchmark + pixel-exactness check (macOS / Linux). Windows: run-bench.ps1.
#
#   GODOT=/path/to/Godot spikes/S1-renderer/run-bench.sh            # full matrix
#   QUICK=1 GODOT=... spikes/S1-renderer/run-bench.sh               # 10k only, short runs (smoke test)
#
# Environment overrides: GODOT, OUT (output dir), RESOLUTIONS ("WxH ..."), RENDERERS ("method:driver ..."),
# BENCH_ARGS (extra user args for --bench, e.g. "--counts=10000 --modes=cpu:array --shadows=mask").
# Exit code is non-zero if any run crashed or any verification failed.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
GODOT="${GODOT:-godot}"
OUT="${OUT:-$here/results/local-$(date +%Y%m%d-%H%M%S)}"
RESOLUTIONS="${RESOLUTIONS:-1920x1080 2560x1440}"
if [[ -z "${RENDERERS:-}" ]]; then
  case "$(uname -s)" in
    Darwin) RENDERERS="gl_compatibility:opengl3 gl_compatibility:opengl3_angle mobile:metal forward_plus:metal" ;;
    *) RENDERERS="gl_compatibility:opengl3 mobile:vulkan forward_plus:vulkan" ;;
  esac
fi
BENCH_ARGS="${BENCH_ARGS:-}"
if [[ -n "${QUICK:-}" ]]; then
  BENCH_ARGS="--counts=10000 --warmup=0.5 --seconds=1.5 $BENCH_ARGS"
fi

mkdir -p "$OUT"
log="$OUT/bench.log"
status=0

dotnet build "$here/S1Renderer.csproj" -nologo -v q || exit 1
# First run of a fresh checkout: import resources (creates .godot/).
if [[ ! -d "$here/.godot/imported" ]]; then
  "$GODOT" --headless --path "$here" --import >>"$log" 2>&1 || true
fi

for r in $RENDERERS; do
  method="${r%%:*}"
  driver="${r#*:}"
  for res in $RESOLUTIONS; do
    echo "== bench $method/$driver $res" | tee -a "$log"
    # shellcheck disable=SC2086
    "$GODOT" --path "$here" --resolution "$res" --position 0,0 --rendering-method "$method" --rendering-driver "$driver" \
      -- --bench --size="$res" --out="$OUT/results.csv" --screenshot="$OUT/frame-$method-$driver-$res.png" $BENCH_ARGS \
      2>&1 | tee -a "$log" | grep -E "^S1(INFO|RESULT|SHOT|ERROR)"
    [[ "${PIPESTATUS[0]}" == "0" ]] || status=1
  done
  echo "== verify $method/$driver" | tee -a "$log"
  "$GODOT" --path "$here" --resolution 1280x720 --position 0,0 --rendering-method "$method" --rendering-driver "$driver" \
    -- --verify --size=1280x720 --shotdir="$OUT" 2>&1 | tee -a "$log" | grep -E "^S1(INFO|VERIFY|ERROR)"
  [[ "${PIPESTATUS[0]}" == "0" ]] || status=1
done

echo "results: $OUT/results.csv (log: $log)"
exit $status
