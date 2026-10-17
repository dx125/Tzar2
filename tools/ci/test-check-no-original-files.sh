#!/usr/bin/env bash
# Regression tests for tools/ci/check-no-original-files.sh, run against throw-away git repositories.
# Usage: tools/ci/test-check-no-original-files.sh   (exit code 0 = all cases pass)
set -euo pipefail

guard="$(cd "$(dirname "$0")" && pwd)/check-no-original-files.sh"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0
cases=0

# Synthetic "original" content; the manifest below lists its size and MD5.
original_content='synthetic original game file'
if command -v md5sum >/dev/null 2>&1; then
  original_md5=$(printf '%s' "$original_content" | md5sum | cut -d' ' -f1)
else
  original_md5=$(printf '%s' "$original_content" | md5 -q)
fi

new_repo() {
  repo="$work/repo$cases"
  mkdir -p "$repo/docs/original-build/manifests" "$repo/src"
  git -C "$repo" init -q
  git -C "$repo" config user.email test@example.invalid
  git -C "$repo" config user.name test
  git -C "$repo" config core.autocrlf false
  printf 'path\tsize\tmd5\nDATA/ORIGINAL.INI\t%s\t%s\n' "${#original_content}" "$original_md5" \
    > "$repo/docs/original-build/manifests/test.tsv"
  printf '/Origin/\n' > "$repo/.gitignore"
  printf 'our own code\n' > "$repo/src/Code.cs"
  git -C "$repo" add -A
  git -C "$repo" commit -q -m base
}

commit_all() {
  git -C "$repo" add -A
  git -C "$repo" commit -q -m "$1"
}

# expect <0|1> <description> [guard args...]   (runs the guard inside $repo)
expect() {
  local want="$1" description="$2" got=0 output
  shift 2
  cases=$((cases + 1))
  output=$(cd "$repo" && env -u TZAR2_ORIGIN_DIR "$guard" "$@" 2>&1) || got=$?
  if [ "$got" -ne 0 ]; then got=1; fi
  if [ "$got" -eq "$want" ]; then
    echo "ok   $description"
  else
    echo "FAIL $description (expected exit $want, got $got)"
    printf '%s\n' "$output" | sed 's/^/     | /'
    failures=$((failures + 1))
  fi
}

new_repo
expect 0 "clean repository passes"

new_repo
mkdir -p "$repo/assets"
printf '%s' "$original_content" > "$repo/assets/renamed copy.bin"
commit_all add
expect 1 "byte-identical copy under another name (with a space) is caught"

new_repo
mkdir -p "$repo/assets"
printf '%s' "$original_content" > "$repo/assets/new"$'\n'"line.bin"
commit_all add
expect 1 "byte-identical copy with a newline in its name is caught"

# GNU md5sum prefixes its output with '\' for such names, which used to corrupt the fingerprint.
new_repo
mkdir -p "$repo/assets"
printf '%s' "$original_content" > "$repo/assets/back\\slash.bin"
commit_all add
expect 1 "byte-identical copy with a backslash in its name is caught"

new_repo
mkdir -p "$repo/assets"
printf 'x' > "$repo/assets/back\\slash.txt"
commit_all add
expect 0 "harmless file with a backslash in its name passes"

new_repo
mkdir -p "$repo/origin"
printf 'x' > "$repo/origin/readme.md"
git -C "$repo" add -f origin/readme.md
git -C "$repo" commit -q -m add
expect 1 "lower-case origin/ folder is caught (case-insensitive)"

for name in pack.wdt MAP.WMP c.wcm s.wsv d.wdm m.ddv sprite.RLE tool.exe lib.DLL font.fnt game.pal; do
  new_repo
  printf 'x' > "$repo/src/$name"
  commit_all add
  expect 1 "denied extension: $name"
done

for path in DATA/RULES.INI IMAGES/UNIT/A.BMP SOUND/hit.wav MUSIC/T1.MID LOCAL/STRINGS.TXT HELP/HK.TXT src/data/ai/x.ais; do
  new_repo
  mkdir -p "$repo/$(dirname "$path")"
  printf 'x' > "$repo/$path"
  commit_all add
  expect 1 "original-looking data file: $path"
done

new_repo
mkdir -p "$repo/docs/notes" "$repo/src/Images"
printf 'x' > "$repo/docs/notes/readme.txt"
printf 'x' > "$repo/src/Images/icon.png"
commit_all add
expect 0 ".txt outside data folders and .png under Images/ pass"

new_repo
mkdir -p "$repo/tests/fixtures/DATA" "$repo/tools/ci"
printf 'x' > "$repo/tests/fixtures/DATA/RULES.INI"
printf 'x' > "$repo/tests/fixtures/fake.wdt"
printf '# synthetic fixtures\ntests/fixtures/*\n' > "$repo/tools/ci/original-files-allowlist.txt"
commit_all add
expect 0 "allowlisted synthetic fixtures pass"

new_repo
mkdir -p "$repo/tests/fixtures" "$repo/tools/ci"
printf '%s' "$original_content" > "$repo/tests/fixtures/real.bin"
printf 'tests/fixtures/*\n' > "$repo/tools/ci/original-files-allowlist.txt"
commit_all add
expect 1 "allowlist does not excuse a byte-identical original file"

new_repo
mkdir -p "$repo/Origin/DATA" "$repo/assets"
printf 'locally modified original' > "$repo/Origin/DATA/MODIFIED.INI"
printf 'locally modified original' > "$repo/assets/copy.bin"
commit_all add
expect 1 "copy of a file that exists only in the local Origin/ folder is caught"

new_repo
base=$(git -C "$repo" rev-parse HEAD)
mkdir -p "$repo/assets"
printf '%s' "$original_content" > "$repo/assets/leak.bin"
commit_all leak
git -C "$repo" rm -q assets/leak.bin
git -C "$repo" commit -q -m remove
expect 0 "HEAD-only check does not see a file removed again"
expect 1 "range check catches a file added and removed within the range" --range "$base..HEAD"
expect 1 "all-zero base (new branch) checks the whole history" --range "0000000000000000000000000000000000000000..HEAD"
expect 1 "unknown base (force push) checks the whole history" --range "1234567890123456789012345678901234567890..HEAD"
expect 0 "range after the removal is clean" --range "HEAD~1..HEAD"

new_repo
base=$(git -C "$repo" rev-parse HEAD)
printf '%s' "$original_content" > "$repo/src/Code.cs"
commit_all modify
printf 'our own code again\n' > "$repo/src/Code.cs"
commit_all restore
expect 1 "range check catches an original file in a modified (not added) blob" --range "$base..HEAD"

echo "$((cases - failures))/$cases cases passed."
[ "$failures" -eq 0 ]
