# 0242. Sign in the pipeline with Azure Trusted Signing or a PFX, and skip signing without secrets

* **Status:** Accepted (the certificate is the owner's to provide; until then builds are unsigned)
* **Date:** 2026-10-22
* **Deciders:** maintainer (Claude); the certificate: product owner
* **Related:** TDD §25 M7; ADRs 0240, 0241; [docs/release/code-signing.md](../release/code-signing.md)

## Context

M7 asks for code signing. Since June 2023 the CA/Browser Forum requires the private key of every publicly trusted
code-signing certificate to live in hardware (token or cloud HSM), so a `.pfx` can no longer be bought and stored as a
CI secret; unattended CI signing needs a cloud HSM with an API. Microsoft's SmartScreen no longer gives EV certificates
an instant reputation, so EV buys nothing over OV for us. The owner has not bought anything yet, and forks and
contributors' PRs never have secrets: the pipeline must build without them.

## Decision

1. **Signing is a step of `windows-package.yml` that skips itself without secrets** and never fails a build for that
   reason. Two routes:
   * **Azure Trusted Signing** (recommended; the official `azure/trusted-signing-action`) when
     `AZURE_SIGNING_ACCOUNT` is set, with `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
     `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_PROFILE`;
   * **a PFX with signtool** (`tools/package/sign.ps1`) when `WINDOWS_SIGNING_PFX_BASE64` and
     `WINDOWS_SIGNING_PFX_PASSWORD` are set, for certificates that still come as files (private CAs, tests); the PFX is
     imported into the runner's user certificate store and signtool picks it by thumbprint, so the password is never on
     a command line.
2. **What is signed:** `Tzar2.exe`, `Tzar2.console.exe` and our assemblies `data_*/Tzar2.*.dll` before the zip and the
   installer are made, then the installer. SHA-256 file digests and RFC 3161 timestamps, so signatures outlive the
   certificate. When a route ran, every executable must then verify as `Valid` or the job fails.
3. **A signing self-test runs on every package build:** `sign.ps1 -SelfTest` makes a throw-away self-signed certificate
   and signs a copy of `Tzar2.exe` that is never shipped, which keeps the PFX route working while no real certificate
   exists.
4. **The owner's part** is in code-signing.md: which certificate to buy, in which order of preference (Azure Trusted
   Signing if he qualifies, otherwise SSL.com OV with eSigner, which needs one more workflow step), and how to add the
   secrets.
5. **Supply chain:** every action of the workflow is pinned by commit (version in a comment), and the Godot downloads
   are checked against the SHA-256 GitHub records for the release assets (review of PR #132).

## Alternatives considered

* **Sign by hand on the owner's PC with a USB token:** no unattended nightly builds, and every beta build waits for the
  owner. Kept as the fallback for Certum's cloud card, which cannot run unattended.
* **EV certificate:** dearer than OV, and no SmartScreen advantage since 2024.
* **Fail the build without a certificate:** contributors' PRs and the beta's start would wait for a purchase.

## Consequences

* Beta builds are unsigned until the owner acts; the invitation tells testers how to pass SmartScreen.
* The Inno Setup uninstaller is not signed yet (it needs Inno's `SignTool` directive with the same route); follow-up.
* The Azure action's inputs are pinned to `v0.5.1`; check them against the action's documentation the first time the
  secrets exist (no run has used them yet).
