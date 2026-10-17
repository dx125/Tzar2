# Fidelity facts (TDD §22.3)

`facts/<scenario>.yaml` are measurements of the original game produced by the capture harness (ADR 0030):

```sh
t2 fidelity analyze capture-YYYYMMDD-HHMMSS.zip --original "$TZAR2_ORIGIN"     # writes tests/fidelity/facts/*.yaml
t2 fidelity analyze capture-....zip --extract tests/fidelity/local-captures/<session>   # raw files for manual review
```

* Fact files hold **numbers only** (tables, counts, fitted rules) plus provenance (REQ-RE-02: edition, reference
  check, session, dates, harness commit, renderer, number of trials). Commit them.
* The capture zips and anything extracted from them (screenshots of original art, memory samples) are local only:
  `tests/fidelity/local-captures/` and `capture-*.zip` are git-ignored (REQ-LEGAL-01).
* A fact becomes a test once the corresponding feature exists: the future `Tzar2.Fidelity.Tests` compare our
  simulation and renderer with these values within the S4/S5 tolerances.
