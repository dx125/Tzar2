#!/usr/bin/env bash
# REQ-LEGAL-01 guard: fails if any file of the original game is (or ever was, within the checked range) committed.
#
# Usage:
#   tools/ci/check-no-original-files.sh                 # local: every tracked file in the working tree
#   tools/ci/check-no-original-files.sh --range A..B    # CI: additionally every blob added/modified by a commit in A..B
#   tools/ci/check-no-original-files.sh --range B       # every commit reachable from B (A all-zero = new branch, same)
#
# Checks, for every path/content checked:
#  1. nothing under Origin/ (any letter case) may be tracked;
#  2. no file may have the size and MD5 of a file listed in docs/original-build/manifests/*.tsv, or of any file in the
#     local, git-ignored Origin/ folder when one exists (so modified and extra local files are caught too; the folder
#     can be moved with TZAR2_ORIGIN_DIR);
#  3. no file may have an extension used only by the original game (packs, maps, campaigns, saves, demos, movies,
#     sprites, executables, fonts, palettes), and no original-looking data file (.bmp/.wav/.mid/.ini/.txt/.dat/.ai/.ais)
#     may sit under a directory named like the original's data folders (DATA/, IMAGES/, SOUND/, ...), unless the path
#     matches a glob in tools/ci/original-files-allowlist.txt (synthetic test fixtures only).
# Empty files are skipped by check 2: an empty file carries no content and would match every empty manifest entry.
set -euo pipefail

range=""
while [ $# -gt 0 ]; do
  case "$1" in
    --range)
      [ $# -ge 2 ] || { echo "usage: $0 [--range <rev-range>]" >&2; exit 2; }
      range="$2"
      shift 2
      ;;
    *)
      echo "usage: $0 [--range <rev-range>]" >&2
      exit 2
      ;;
  esac
done

cd "$(git rev-parse --show-toplevel)"
status=0

if command -v md5sum >/dev/null 2>&1; then
  md5_stdin() { md5sum | cut -d' ' -f1; }
else
  md5_stdin() { md5 -q; }
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# --- Allowlist (globs; '#' comments and blank lines ignored) ---------------------------------------------------------
allow=()
if [ -f tools/ci/original-files-allowlist.txt ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    case "$line" in '' | '#'*) continue ;; esac
    allow+=("$line")
  done < tools/ci/original-files-allowlist.txt
fi

is_allowed() {
  local pattern
  for pattern in ${allow[@]+"${allow[@]}"}; do
    # shellcheck disable=SC2053 # glob match is intended
    if [[ "$1" == $pattern ]]; then
      return 0
    fi
  done
  return 1
}

# --- Path rules (1 and 3) --------------------------------------------------------------------------------------------
denied_extensions=" wdt wmp wcm wsv wdm ddv rle exe dll fnt pal "
data_extensions=" bmp wav mid ini txt dat ai ais "
data_dirs=" data images sound sounds music local help movies "

# Prints the reason when the path breaks a path rule.
path_violation() {
  local path="$1" lower base ext dir
  lower=$(printf '%s' "$path" | tr '[:upper:]' '[:lower:]')
  case "$lower" in
    origin/*) echo "is under Origin/"; return ;;
  esac
  base="${lower##*/}"
  case "$base" in
    *.*) ext="${base##*.}" ;;
    *) return ;;
  esac
  if is_allowed "$path"; then
    return
  fi
  if [[ "$denied_extensions" == *" $ext "* ]]; then
    echo "has the original-game extension .$ext"
    return
  fi
  if [[ "$data_extensions" == *" $ext "* && "$lower" == */* ]]; then
    local rest="${lower%/*}"
    while [ -n "$rest" ]; do
      dir="${rest##*/}"
      if [[ "$data_dirs" == *" $dir "* ]]; then
        echo "looks like an original data file (.$ext under $dir/)"
        return
      fi
      [[ "$rest" == */* ]] || break
      rest="${rest%/*}"
    done
  fi
}

# --- Collect what to check -------------------------------------------------------------------------------------------
# Records in $tmp/entries: "<kind>\0<ref>\0<path>\0" where kind is f (working-tree file, ref = path) or b (blob, ref = sha).
: > "$tmp/entries"
while IFS= read -r -d '' file; do
  printf 'f\0%s\0%s\0' "$file" "$file" >> "$tmp/entries"
done < <(git ls-files -z)

if [ -n "$range" ]; then
  # A new branch (all-zero base) or a base that no longer exists (force push): check the whole history of the tip.
  if [[ "$range" =~ ^([^.]+)\.\.([^.].*)$ ]] && ! git cat-file -e "${BASH_REMATCH[1]}^{commit}" 2>/dev/null; then
    echo "Base '${BASH_REMATCH[1]}' is not a known commit; checking the whole history of '${BASH_REMATCH[2]}'."
    range="${BASH_REMATCH[2]}"
  fi
  commits=$(git rev-list "$range")
  echo "Checking every blob added or modified by $(printf '%s' "$commits" | grep -c . || true) commit(s) in '$range'..."
  for commit in $commits; do
    # -m: also list the changes a merge commit makes against each parent. Output with -z: ":<modes> <src> <dst> <status>\0<path>\0".
    while IFS= read -r -d '' meta && IFS= read -r -d '' path; do
      blob=$(printf '%s' "$meta" | cut -d' ' -f4)
      mode=$(printf '%s' "$meta" | cut -d' ' -f2)
      [ "$mode" != "160000" ] || continue # submodule
      printf 'b\0%s\0%s\0' "$blob" "$path" >> "$tmp/entries"
    done < <(git diff-tree -r -z -m --root --no-commit-id --diff-filter=AM "$commit")
  done
fi

entry_size() {
  if [ "$1" = f ]; then wc -c < "$2" | tr -d ' '; else git cat-file -s "$2"; fi
}
entry_md5() {
  if [ "$1" = f ]; then md5_stdin < "$2"; else git cat-file blob "$2" | md5_stdin; fi
}

# Sizes of everything checked (to fingerprint only same-sized files of the local Origin/ folder).
: > "$tmp/sizes"
while IFS= read -r -d '' kind && IFS= read -r -d '' ref && IFS= read -r -d '' path; do
  if [ "$kind" = f ] && [ ! -f "$ref" ]; then continue; fi
  entry_size "$kind" "$ref" >> "$tmp/sizes"
done < "$tmp/entries"
sort -u -o "$tmp/sizes" "$tmp/sizes"

# --- Known fingerprints ("size:md5") ---------------------------------------------------------------------------------
for manifest in docs/original-build/manifests/*.tsv; do
  [ -f "$manifest" ] || continue
  awk -F'\t' 'NR == 1 { for (i = 1; i <= NF; i++) { if ($i == "size") s = i; if ($i == "md5") m = i } next }
              s && m && $s > 0 { print $s ":" tolower($m) }' "$manifest"
done > "$tmp/known"
manifest_count=$(sort -u "$tmp/known" | wc -l | tr -d ' ')

origin_dir="${TZAR2_ORIGIN_DIR:-Origin}"
origin_count=0
if [ -d "$origin_dir" ]; then
  while IFS= read -r -d '' file; do
    origin_count=$((origin_count + 1))
    size=$(wc -c < "$file" | tr -d ' ')
    [ "$size" -gt 0 ] || continue
    if grep -qxF -- "$size" "$tmp/sizes"; then
      echo "$size:$(md5_stdin < "$file")" >> "$tmp/known"
    fi
  done < <(find "$origin_dir" -type f -print0)
fi
sort -u -o "$tmp/known" "$tmp/known"
echo "Fingerprints: $manifest_count from the manifests; local Origin/ folder: $([ -d "$origin_dir" ] && echo "$origin_count files" || echo "not present")."

# --- Check -----------------------------------------------------------------------------------------------------------
checked=0
while IFS= read -r -d '' kind && IFS= read -r -d '' ref && IFS= read -r -d '' path; do
  where="'$path'"
  [ "$kind" = f ] || where="'$path' (blob $ref, in history)"
  reason=$(path_violation "$path")
  if [ -n "$reason" ]; then
    echo "ERROR: $where $reason (REQ-LEGAL-01)."
    status=1
  fi
  if [ "$kind" = f ] && [ ! -f "$ref" ]; then continue; fi
  checked=$((checked + 1))
  size=$(entry_size "$kind" "$ref")
  [ "$size" -gt 0 ] || continue
  if grep -q -- "^$size:" "$tmp/known" && grep -qxF -- "$size:$(entry_md5 "$kind" "$ref")" "$tmp/known"; then
    echo "ERROR: $where is byte-identical to a file of the original game (REQ-LEGAL-01)."
    status=1
  fi
done < "$tmp/entries"

if [ "$status" -eq 0 ]; then
  echo "OK: none of $checked checked files is an original game file."
fi
exit "$status"
