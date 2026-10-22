<#
.SYNOPSIS
  Signs Windows files with signtool and a PFX certificate (ADR 0242). Skips, successfully, when no certificate is set.

.DESCRIPTION
  The certificate comes from the environment (GitHub secrets in the workflow):
    WINDOWS_SIGNING_PFX_BASE64    the .pfx file, base64-encoded
    WINDOWS_SIGNING_PFX_PASSWORD  its password
    WINDOWS_SIGNING_TIMESTAMP     RFC 3161 timestamp server (default http://timestamp.digicert.com)
  Without WINDOWS_SIGNING_PFX_BASE64 it prints that signing was skipped and exits 0. The other route, Azure Trusted
  Signing (a cloud HSM, no PFX), is the workflow's own step (azure/trusted-signing-action); see docs/release/code-signing.md.
  -SelfTest makes a throw-away self-signed certificate, signs the given files with it and checks that a signature is
  there (not that it is trusted): the CI proves the script works without any secret.

.EXAMPLE
  pwsh tools/package/sign.ps1 -Files artifacts/windows/Tzar2/Tzar2.exe, artifacts/windows/Tzar2/Tzar2.console.exe
#>
param(
    [Parameter(Mandatory)][string[]]$Files,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) { throw "signtool.exe was not found (Windows SDK)." }
    return $found.FullName
}

# The password never reaches a command line: the PFX is imported into the current user's certificate store (the password
# goes to Import-PfxCertificate as a SecureString), signtool picks the certificate by its thumbprint, and the
# certificate is removed from the store afterwards.
$thumbprint = $null
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("tzar2-sign-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $temp | Out-Null
try {
    if ($SelfTest) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Tzar2 signing self-test" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(1)
        $thumbprint = $cert.Thumbprint
    }
    elseif ($env:WINDOWS_SIGNING_PFX_BASE64) {
        $pfx = Join-Path $temp "signing.pfx"
        [System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_SIGNING_PFX_BASE64))
        $secure = ConvertTo-SecureString $env:WINDOWS_SIGNING_PFX_PASSWORD -AsPlainText -Force
        $cert = Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\CurrentUser\My -Password $secure
        Remove-Item $pfx -Force
        $thumbprint = ($cert | Where-Object { $_.HasPrivateKey } | Select-Object -First 1).Thumbprint
        if (-not $thumbprint) { throw "The PFX holds no certificate with a private key." }
    }
    else {
        Write-Host "Code signing skipped: WINDOWS_SIGNING_PFX_BASE64 is not set (ADR 0242)."
        exit 0
    }

    $signtool = Find-SignTool
    $timestamp = if ($env:WINDOWS_SIGNING_TIMESTAMP) { $env:WINDOWS_SIGNING_TIMESTAMP } else { "http://timestamp.digicert.com" }
    foreach ($file in $Files) {
        if (-not (Test-Path $file)) { throw "No file to sign at $file" }
        $arguments = @("sign", "/fd", "SHA256", "/s", "My", "/sha1", $thumbprint)
        if (-not $SelfTest) { $arguments += @("/tr", $timestamp, "/td", "SHA256") }
        $arguments += @("/d", "Tzar2", "/du", "https://github.com/dx125/Tzar2", $file)
        & $signtool @arguments
        if ($LASTEXITCODE -ne 0) { throw "signtool sign failed for $file (exit $LASTEXITCODE)" }

        if ($SelfTest) {
            $signature = Get-AuthenticodeSignature $file
            if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -ne "CN=Tzar2 signing self-test") {
                throw "self-test: $file carries no signature after signing"
            }
            Write-Host "self-test: $file signed ($($signature.Status), expected for a self-signed certificate)"
        }
        else {
            & $signtool verify /pa /v $file
            if ($LASTEXITCODE -ne 0) { throw "signtool verify failed for $file (exit $LASTEXITCODE)" }
        }
    }
}
finally {
    if ($thumbprint) { Remove-Item "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue }
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
