# Code signing the Windows build

How the Windows package is signed (ADR 0242), what the owner has to buy or provide, and how to hand it to the
pipeline. Until a certificate is in place every build is unsigned: the signing steps of
`.github/workflows/windows-package.yml` print "skipped" and the rest of the pipeline runs as usual.

## What an unsigned build means for testers

Windows SmartScreen shows "Windows protected your PC" for an unsigned installer or `Tzar2.exe` downloaded from the
internet; the tester clicks **More info → Run anyway**. That is acceptable for a closed beta of trusted veterans (say so
in the invitation), not for a public release. A signature names the publisher and lets the download build a SmartScreen
reputation; the warning still shows for a new certificate until enough people have run the signed files.

## What to buy

Since June 2023 the CA/Browser Forum requires that the private key of every publicly trusted code-signing certificate
(OV and EV alike) lives in hardware (a USB token or a cloud HSM). A certificate can no longer be bought as a `.pfx`
file and put into a CI secret. For a CI pipeline that signs unattended, the key must be in a **cloud HSM with an API**.
EV certificates no longer skip the SmartScreen reputation either, so EV is not worth its price for this project.

Pick the first option the owner qualifies for:

1. **Azure Trusted Signing (recommended).** Microsoft's own signing service: the certificate is issued and renewed by
   Microsoft, the key never leaves Azure, and the official GitHub action signs in CI. Basic tier about USD 10 per month
   (5,000 signatures; check the current price). Identity validation is required and is only open to **organisations
   with at least three years of verifiable history** and to **individual developers in a limited set of countries**
   (the USA and Canada when last checked); check the current list in the Azure portal before deciding.
   The pipeline supports it as is.
2. **SSL.com OV code signing with eSigner** (cloud HSM), if Trusted Signing is not open to the owner. Available to
   individuals and organisations in most countries; OV certificate plus an eSigner subscription (check prices).
   Signing in CI uses SSL.com's `CodeSignTool` with the account's user name, password, TOTP secret and credential id.
   This route needs one more step in the workflow (about 20 lines, `CodeSignTool sign -input_file_path=...`); ask the
   maintainer to add it when the certificate exists.
3. **Certum "Open Source Code Signing in the Cloud"**: cheap and open to individuals for open-source projects, but its
   SimplySign virtual card needs an interactive login, so it does not run unattended in CI. Only worth it if the owner
   signs releases by hand on his PC.

The certificate is issued to the owner (or his company): the publisher name shown by Windows is that name. The project's
name is not decided yet (TDD Q-01); the certificate does not depend on it.

## Hand it to the pipeline

GitHub → `dx125/Tzar2` → **Settings → Secrets and variables → Actions → New repository secret**, or with the GitHub CLI
from any checkout (`gh secret set NAME --repo dx125/Tzar2`, then paste the value). Secrets are never shown again, not in
logs either.

### Azure Trusted Signing

In the Azure portal: create a **Trusted Signing account** (note its region endpoint, e.g.
`https://weu.codesigning.azure.net/`), complete **Identity validation** (Public), create a **Certificate profile** of type
Public Trust, then an **App registration** (Microsoft Entra ID) with a client secret, and give that app the role
**Trusted Signing Certificate Profile Signer** on the account. Then set six secrets:

| Secret | Value |
|---|---|
| `AZURE_TENANT_ID` | the directory (tenant) id of the app registration |
| `AZURE_CLIENT_ID` | the application (client) id |
| `AZURE_CLIENT_SECRET` | the client secret's value |
| `AZURE_SIGNING_ENDPOINT` | the account's endpoint, e.g. `https://weu.codesigning.azure.net/` |
| `AZURE_SIGNING_ACCOUNT` | the Trusted Signing account's name (its presence switches the step on) |
| `AZURE_SIGNING_PROFILE` | the certificate profile's name |

### A PFX certificate

For a certificate that does come as a `.pfx` (a private CA, a test certificate): base64-encode it and set two secrets.

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\path\to\certificate.pfx")) | Set-Clipboard
gh secret set WINDOWS_SIGNING_PFX_BASE64 --repo dx125/Tzar2      # paste
gh secret set WINDOWS_SIGNING_PFX_PASSWORD --repo dx125/Tzar2    # the .pfx password
```

Optional: `WINDOWS_SIGNING_TIMESTAMP` (an RFC 3161 server; default `http://timestamp.digicert.com`).

## What gets signed, and how to check it

`Tzar2.exe`, `Tzar2.console.exe` and our own assemblies (`data_*/Tzar2.*.dll`) before they are zipped and packed into the
installer, then the installer itself. The Inno Setup uninstaller is not signed yet (it needs Inno's `SignTool`
directive; follow-up). The workflow verifies every signature (`signtool verify /pa`) and fails if one is not valid.
A tester can check by hand: right-click `Tzar2.exe` → Properties → **Digital Signatures**.

The workflow also runs a **signing self-test** on every package build: a throw-away self-signed certificate signs a
copy of `Tzar2.exe` that is never shipped, which proves the PFX route works without any secret.
