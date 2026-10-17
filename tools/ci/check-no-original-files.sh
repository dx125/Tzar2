#!/usr/bin/env bash
# REQ-LEGAL-01 guard: fails if any file of the original game is tracked by git.
#  1. nothing under Origin/ may be tracked;
#  2. no tracked file may have the size and MD5 of a file listed in docs/original-build/manifests/*.tsv.
# Empty files are skipped: an empty file carries no content and would match every empty manifest entry.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
status=0

origin_files=$(git ls-files -- 'Origin/' 'Origin/**')
if [ -n "$origin_files" ]; then
  echo "ERROR: files under Origin/ are tracked (REQ-LEGAL-01):"
  echo "$origin_files"
  status=1
fi

if command -v md5sum >/dev/null 2>&1; then
  md5_of() { md5sum "$1" | cut -d' ' -f1; }
else
  md5_of() { md5 -q "$1"; }
fi

# "size:md5" keys of every file in the reference manifests (columns located by header name).
known=$(mktemp)
trap 'rm -f "$known"' EXIT
for manifest in docs/original-build/manifests/*.tsv; do
  awk -F'\t' 'NR == 1 { for (i = 1; i <= NF; i++) { if ($i == "size") s = i; if ($i == "md5") m = i } next }
              s && m && $s > 0 { print $s ":" tolower($m) }' "$manifest"
done | sort -u > "$known"
echo "Checking tracked files against $(wc -l < "$known" | tr -d ' ') known original-file fingerprints..."

while IFS= read -r -d '' file; do
  [ -f "$file" ] || continue
  size=$(wc -c < "$file" | tr -d ' ')
  [ "$size" -gt 0 ] || continue
  key="$size:$(md5_of "$file")"
  if grep -qxF "$key" "$known"; then
    echo "ERROR: '$file' is byte-identical to a file of the original game (REQ-LEGAL-01)."
    status=1
  fi
done < <(git ls-files -z)

if [ "$status" -eq 0 ]; then
  echo "OK: no original game files are tracked."
fi
exit "$status"
