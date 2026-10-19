# Regenerates docs/decisions/README.md, the index of the architecture decision records.
# Run after adding or changing an ADR:  pwsh tools/docs/adr-index.ps1
# Reads the title, status and date lines that every ADR starts with (docs/decisions/0000-template.md).
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$dir = Join-Path $root "docs/decisions"
$rows = foreach ($f in Get-ChildItem $dir -Filter "*.md" | Where-Object { $_.Name -match '^\d{4}-' -and $_.Name -ne '0000-template.md' } | Sort-Object Name) {
    $lines = [IO.File]::ReadAllLines($f.FullName)
    $title = ($lines | Where-Object { $_ -match '^# ' } | Select-Object -First 1) -replace '^# \d{4}\.\s*', ''
    $statusLine = ($lines | Where-Object { $_ -match '^\* \*\*Status:\*\*' } | Select-Object -First 1) -replace '^\* \*\*Status:\*\*\s*', ''
    $status = if ($statusLine -match '^(Superseded[^.;]*|Deprecated|Proposed|Accepted)') { $Matches[1] } else { 'see ADR' }
    if ($statusLine -match '^Accepted' -and $statusLine.Length -gt 'Accepted'.Length + 1) { $status = 'Accepted (with notes)' }
    $date = ($lines | Where-Object { $_ -match '^\* \*\*Date:\*\*' } | Select-Object -First 1) -replace '^\* \*\*Date:\*\*\s*', ''
    $num = $f.Name.Substring(0, 4)
    $title = $title.Replace('|', '\|')
    "| [$num]($($f.Name)) | $title | $status | $date |"
}
$out = @(
    '# Architecture decision records'
    ''
    'Every decision that shapes the code, with its context, the alternatives and the consequences. Start a new one from'
    '[0000-template.md](0000-template.md) with the next free number, then regenerate this index with'
    '`pwsh tools/docs/adr-index.ps1`. A decision that changes an earlier one supersedes it (status line of both).'
    ''
    '"Accepted (with notes)" means the status line qualifies the acceptance (a provisional part, a follow-up); read it.'
    ''
    '| ADR | Decision | Status | Date |'
    '|---|---|---|---|'
) + $rows
[IO.File]::WriteAllText((Join-Path $dir "README.md"), (($out -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
"wrote docs/decisions/README.md ($(@($rows).Count) ADRs)"
