#!/usr/bin/env bash
# Nightly benchmark gate (TDD §21, ADR 0080) for the `t2 bench` output(s) of one scenario.
#
# Usage:
#   tools/ci/check-bench.sh <name> <p99-budget-ms> [--informational] <bench-output>...
#
# With several outputs (repeated runs of the same scenario), the run with the lowest p50 is checked: noise on a shared
# runner only ever adds time, so the best of two runs filters most of it.
#
# Checks:
#  1. absolute budget: the tick p99 must not exceed <p99-budget-ms> (the 4-vCPU runner is weaker than the 6-core
#     reference CPU of TDD §4.3, so passing here implies passing there);
#  2. relative regression (TDD §21 "regressions > 10% fail the nightly") against tools/ci/bench-baseline.tsv:
#     - a baseline row for this runner's CPU model: the tick p50 must stay within 10% of it (same hardware);
#     - otherwise (a CPU model not seen yet): the calibration-normalized p50 must stay within 25% of the slowest
#       recorded normalized p50 (normalization leaves about 20% between CPU models, so this only catches big steps);
#     - no row for the name at all: report only.
# With --informational, failures are reported as warnings and the script exits 0 (the stress scenario).
# Each run prints the "baseline" row for its CPU, to paste into the TSV when a change is meant to cost time.
set -euo pipefail

usage() { echo "usage: $0 <name> <p99-budget-ms> [--informational] <bench-output>..." >&2; exit 2; }
[ $# -ge 3 ] || usage
name="$1"
budget="$2"
shift 2
informational=""
if [ "$1" = "--informational" ]; then
  informational="$1"
  shift
fi
[ $# -ge 1 ] || usage
baseline_file="$(dirname "$0")/bench-baseline.tsv"

# The run with the lowest tick p50.
file=""
best=""
for candidate in "$@"; do
  value="$(grep '^tick ms' "$candidate" | sed -E 's/.*p50 ([0-9.]+).*/\1/' || true)"
  [ -n "$value" ] || { echo "::error::$name: no tick line in $candidate"; exit 1; }
  if [ -z "$best" ] || awk -v v="$value" -v b="$best" 'BEGIN { exit !(v < b) }'; then
    best="$value"
    file="$candidate"
  fi
done

p50="$(grep '^tick ms' "$file" | sed -E 's/.*p50 ([0-9.]+).*/\1/' || true)"
p99="$(grep '^tick ms' "$file" | sed -E 's/.*p99 ([0-9.]+).*/\1/' || true)"
norm_p50="$(grep '^normalized' "$file" | sed -E 's/.*p50 ([0-9.]+).*/\1/' || true)"
cpu="$(grep '^cpu ' "$file" | sed -E 's/^cpu +//; s/, [0-9]+ logical processors$//' || true)"
[ -n "$p50" ] && [ -n "$p99" ] && [ -n "$norm_p50" ] && [ -n "$cpu" ] || { echo "::error::$name: incomplete t2 bench output in $file"; exit 1; }

level=error
[ "$informational" = "--informational" ] && level=warning
status=0

fail() {
  echo "::$level::$1"
  status=1
}

over() { awk -v v="$1" -v l="$2" 'BEGIN { exit !(v > l) }'; }

echo "$name on $cpu (best of $#: $file): p50 $p50 ms, p99 $p99 ms (budget $budget ms), normalized p50 $norm_p50 ms"
if over "$p99" "$budget"; then
  fail "$name tick p99 $p99 ms exceeds the $budget ms budget"
fi

same="$(awk -F'\t' -v n="$name" -v c="$cpu" '$1 == n && $2 == c { print $3 }' "$baseline_file")"
slowest="$(awk -F'\t' -v n="$name" '$1 == n && $4 > m { m = $4 } END { if (m > 0) print m }' "$baseline_file")"
if [ -n "$same" ]; then
  limit="$(awk -v b="$same" 'BEGIN { printf "%.3f", b * 1.10 }')"
  echo "$name: baseline p50 $same ms on this CPU, limit $limit ms"
  if over "$p50" "$limit"; then
    fail "$name tick p50 $p50 ms is more than 10% above the baseline $same ms on $cpu"
  fi
elif [ -n "$slowest" ]; then
  limit="$(awk -v b="$slowest" 'BEGIN { printf "%.3f", b * 1.25 }')"
  echo "$name: no baseline for this CPU; normalized limit $limit ms (slowest recorded $slowest ms + 25%)"
  if over "$norm_p50" "$limit"; then
    fail "$name normalized tick p50 $norm_p50 ms is more than 25% above the slowest normalized baseline $slowest ms"
  fi
else
  echo "$name: no baseline"
fi

# The row for tools/ci/bench-baseline.tsv (name, CPU model, p50, normalized p50).
printf 'baseline\t%s\t%s\t%s\t%s\n' "$name" "$cpu" "$p50" "$norm_p50"

if [ "$informational" = "--informational" ]; then
  exit 0
fi
exit $status
