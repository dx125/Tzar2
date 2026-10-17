# 0006. Semantics of `t2 verify-original`

* **Status:** Accepted
* **Date:** 2026-10-17
* **Deciders:** implementation agent (M0)
* **Related:** REQ-RE-03, REQ-IMP-01, RE-13, M0 exit criteria, `docs/original-build/README.md`

## Context

M0 requires a clean reference copy that passes `t2 verify-original` against
`docs/original-build/manifests/gog-1.10-en.reference.tsv` (path, size, MD5). The README's PowerShell snippet reports
only missing and modified files. The original is a Windows game, so file-name case varies between extraction tools.

## Decision

* The comparison logic lives in `Tzar2.Import.Editions` (`ReferenceManifest`, `InstallVerifier`), where edition
  fingerprinting (REQ-IMP-01) will build on it; the CLI only formats the report.
* The reference manifest is **embedded** (it is metadata only), once, in `Tzar2.Import` (`EditionCatalog.Reference`, shared with edition detection) and used by `t2`; `--manifest <file>` compares against another
  manifest, e.g. a GOG depot list.
* Paths match **case-insensitively**; if several local files differ only in case, the exact-case one wins and the
  others are reported as extra.
* Root-level GOG Galaxy files (`goggame-*`) are ignored, because the reference manifest excludes them. OS junk
  (`.DS_Store`, `Thumbs.db`, `desktop.ini`, any depth, any case) is ignored too unless the manifest lists it.
* Manifest paths must be normalized relative paths: forward slashes, no leading `/`, no empty, `.` or `..` segments
  (`a//b`, `./a`, `a/` are rejected).
* The size is compared first; the MD5 is computed only when sizes match.
* **Exit codes:** `0` all reference files present and identical; `1` modified or missing files (also extra files with
  `--strict`); `2` usage or I/O error (including paths the OS cannot represent, e.g. containing NUL, and an option
  where `--manifest` expects its file); `t2` without arguments prints the usage to stderr and exits `2`; `3` (other subcommands) not implemented yet. Extra files are listed but do not
  fail by default, so an install with the HD patch or saved games still counts as clean as long as no original file is
  touched.

## Alternatives considered

* **Extra files fail by default:** stricter, but any user install (saves, logs, patches) would fail even though the
  reference data is intact, which is what REQ-RE-03 cares about.
* **Case-sensitive matching:** would report correct installs extracted by tools that change case as broken.

## Questions for the product owner

* Should extra files make `verify-original` fail by default (today only with `--strict`)?
