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

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("tzar2-sign-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $temp | Out-Null
try {
    if ($SelfTest) {
        $password = [guid]::NewGuid().ToString("N")
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Tzar2 signing self-test" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(1)
        $pfx = Join-Path $temp "selftest.pfx"
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
    }
    elseif ($env:WINDOWS_SIGNING_PFX_BASE64) {
        $pfx = Join-Path $temp "signing.pfx"
        [System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_SIGNING_PFX_BASE64))
        $password = $env:WINDOWS_SIGNING_PFX_PASSWORD
    }
    else {
        Write-Host "Code signing skipped: WINDOWS_SIGNING_PFX_BASE64 is not set (ADR 0242)."
        exit 0
    }

    $signtool = Find-SignTool
    $timestamp = if ($env:WINDOWS_SIGNING_TIMESTAMP) { $env:WINDOWS_SIGNING_TIMESTAMP } else { "http://timestamp.digicert.com" }
    foreach ($file in $Files) {
        if (-not (Test-Path $file)) { throw "No file to sign at $file" }
        $arguments = @("sign", "/fd", "SHA256", "/f", $pfx, "/p", $password)
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
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
