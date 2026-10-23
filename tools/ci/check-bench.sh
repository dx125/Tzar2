#!/usr/bin/env bash
# Nightly benchmark gate (TDD §21, ADR 0080) for the `t2 bench` outputs of one scenario.
#
# Usage:
#   tools/ci/check-bench.sh <name> <p99-budget-ms> [--informational] <bench-output>...
#
# The outputs are repeated runs of the same scenario (the nightly runs three). The checked p50 and p99 are the medians
# over the runs, the same statistic the baselines in tools/ci/bench-baseline.tsv are recorded with (the median of
# single runs), so a tolerance means what it says.
#
# Checks:
#  1. absolute budget: the median tick p99 must not exceed <p99-budget-ms> (the 4-vCPU runner is weaker than the 6-core
#     reference CPU of TDD §4.3, so passing here implies passing there);
#  2. relative regression (TDD §21 "regressions > 10% fail the nightly") against the baseline row for the runner's CPU
#     model, on the calibration-normalized times: normalized p50 <= row normalized p50 x p50 tolerance and normalized
#     p99 <= row normalized p99 x p99 tolerance (the row's own tolerances: 1.10 / 1.30 for rows recorded from at least
#     three runs; provisional rows, recorded from fewer, are looser and warn). Runners of one CPU model differ in speed
#     by up to ~30% from night to night; the calibration measures that night's runner, so the normalized times stay
#     within a few percent where the raw ones do not (ADR 0080 amendment 1). The row's normalized p99 is its p99 scaled
#     as its p50 was (p99 x normalized p50 / p50);
#  3. a CPU model without a row (warns, so the row gets recorded): the calibration-normalized p50 must stay within 35%
#     of the median normalized p50 of the recorded models. Normalization leaves up to ~35% between models, so this only
#     catches large steps until the model's row exists.
# A name without any row only reports. With --informational, failures are warnings and the script exits 0.
# Every check prints the row for its CPU ("baseline ..."), see ADR 0080 for recording rows.
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

level=error
[ "$informational" = "--informational" ] && level=warning
status=0
fail() {
  echo "::$level::$1"
  status=1
}
over() { awk -v v="$1" -v l="$2" 'BEGIN { exit !(v > l) }'; }
median() { sort -g | awk '{ v[NR] = $1 } END { if (NR == 0) exit 1; if (NR % 2) print v[(NR + 1) / 2]; else printf "%.3f\n", (v[NR / 2] + v[NR / 2 + 1]) / 2 }'; }

p50s=""
p99s=""
norms=""
norm99s=""
cpu=""
for f in "$@"; do
  p50="$(grep '^tick ms' "$f" | sed -E 's/.*p50 ([0-9.]+).*/\1/' || true)"
  p99="$(grep '^tick ms' "$f" | sed -E 's/.*p99 ([0-9.]+).*/\1/' || true)"
  norm="$(grep '^normalized' "$f" | sed -E 's/.*p50 ([0-9.]+).*/\1/' || true)"
  norm99="$(grep '^normalized' "$f" | sed -E 's/.*p99 ([0-9.]+).*/\1/' || true)"
  c="$(grep '^cpu ' "$f" | sed -E 's/^cpu +//; s/, [0-9]+ logical processors$//' || true)"
  [ -n "$p50" ] && [ -n "$p99" ] && [ -n "$norm" ] && [ -n "$norm99" ] && [ -n "$c" ] || { echo "::error::$name: incomplete t2 bench output in $f"; exit 1; }
  [ -z "$cpu" ] || [ "$cpu" = "$c" ] || { echo "::error::$name: runs on different CPUs"; exit 1; }
  cpu="$c"
  p50s="$p50s$p50"$'\n'
  p99s="$p99s$p99"$'\n'
  norms="$norms$norm"$'\n'
  norm99s="$norm99s$norm99"$'\n'
done
p50="$(printf '%s' "$p50s" | median)"
p99="$(printf '%s' "$p99s" | median)"
norm_p50="$(printf '%s' "$norms" | median)"
norm_p99="$(printf '%s' "$norm99s" | median)"

echo "$name on $cpu (median of $# runs): p50 $p50 ms, p99 $p99 ms (budget $budget ms), normalized p50 $norm_p50 ms, p99 $norm_p99 ms"
if over "$p99" "$budget"; then
  fail "$name tick p99 $p99 ms exceeds the $budget ms budget"
fi

# Row columns: name, cpu, runs, p50, p99, normalized p50, p50 tolerance, p99 tolerance.
row="$(awk -F'\t' -v n="$name" -v c="$cpu" '$1 == n && $2 == c { print; exit }' "$baseline_file")"
if [ -n "$row" ]; then
  IFS=$'\t' read -r _ _ runs base_p50 base_p99 base_norm tol_p50 tol_p99 <<< "$row"
  base_norm99="$(awk -v p="$base_p99" -v n="$base_norm" -v r="$base_p50" 'BEGIN { printf "%.3f", p * n / r }')"
  limit_p50="$(awk -v b="$base_norm" -v t="$tol_p50" 'BEGIN { printf "%.3f", b * t }')"
  limit_p99="$(awk -v b="$base_norm99" -v t="$tol_p99" 'BEGIN { printf "%.3f", b * t }')"
  echo "$name: baseline on this CPU normalized p50 $base_norm ms (limit $limit_p50), p99 $base_norm99 ms (limit $limit_p99), from $runs runs; raw p50 $p50 vs $base_p50 ms, p99 $p99 vs $base_p99 ms"
  if [ "$runs" -lt 3 ]; then
    echo "::warning::$name: the baseline row for $cpu is provisional ($runs runs; tolerances $tol_p50 / $tol_p99); record it from three runs (ADR 0080)"
  fi
  if over "$norm_p50" "$limit_p50"; then
    fail "$name normalized tick p50 $norm_p50 ms exceeds $limit_p50 ms (baseline $base_norm ms x $tol_p50 on $cpu)"
  fi
  if over "$norm_p99" "$limit_p99"; then
    fail "$name normalized tick p99 $norm_p99 ms exceeds $limit_p99 ms (baseline $base_norm99 ms x $tol_p99 on $cpu)"
  fi
else
  reference="$(awk -F'\t' -v n="$name" '$1 == n { print $6 }' "$baseline_file" | median || true)"
  if [ -n "$reference" ]; then
    limit="$(awk -v b="$reference" 'BEGIN { printf "%.3f", b * 1.35 }')"
    echo "::warning::$name: no baseline row for $cpu; only the loose normalized check applies (limit $limit ms, median of the recorded models + 35%); record this CPU's row (ADR 0080)"
    if over "$norm_p50" "$limit"; then
      fail "$name normalized tick p50 $norm_p50 ms exceeds $limit ms (median normalized baseline $reference ms + 35%)"
    fi
  else
    echo "$name: no baseline"
  fi
fi

# The row for tools/ci/bench-baseline.tsv from this check (tolerances: see ADR 0080).
printf 'baseline\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$cpu" "$#" "$p50" "$p99" "$norm_p50"

if [ "$informational" = "--informational" ]; then
  exit 0
fi
exit $status
