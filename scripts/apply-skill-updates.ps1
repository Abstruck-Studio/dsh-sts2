param(
    [string]$Target = (Join-Path $env:USERPROFILE '.dsh\skills\slay-the-spire-2\SKILL.md'),
    [string]$Append = (Join-Path $PSScriptRoot '..\skill\APPEND.md'),
    [string]$Patches = (Join-Path $PSScriptRoot '..\skill\PATCHES-2026-10-26.json'),
    [switch]$Check
)
$ErrorActionPreference = 'Stop'

# ASCII-only script on purpose: Windows PowerShell 5.1 decodes a BOM-less .ps1 as ANSI, and a CJK
# comment can then swallow the following line. All CJK lives in the JSON/MD payloads below,
# which are read with an explicit UTF-8 decoder.
#
# EOL note: the skill file uses CRLF. Matching is done on an LF-normalised copy, then converted
# back, so the patches do not care which EOL the file (or this script) happens to have.

$Begin = '<!-- sts2-tools-2026-10-26:begin -->'
$End = '<!-- sts2-tools-2026-10-26:end -->'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($p in @($Target, $Append, $Patches)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing: $p" }
}

$raw = [System.IO.File]::ReadAllText($Target, $utf8)
$hadCrlf = $raw.Contains("`r`n")
$work = $raw.Replace("`r`n", "`n")

$block = [System.IO.File]::ReadAllText($Append, $utf8).TrimEnd().Replace("`r`n", "`n")
$spec = [System.IO.File]::ReadAllText($Patches, $utf8) | ConvertFrom-Json
$wrapped = $Begin + "`n" + $block + "`n" + $End

$hasBlock = $work.Contains($Begin)
$applied = @()
$skipped = @()
$missing = @()

if ($Check) {
    Write-Host ("target  : {0}" -f $Target)
    Write-Host ("eol     : {0}" -f $(if ($hadCrlf) { 'CRLF' } else { 'LF' }))
    Write-Host ("block   : {0}" -f $(if ($hasBlock) { 'present' } else { 'absent' }))
    foreach ($patch in $spec.patches) {
        $state = if ($work.Contains($patch.marker)) { 'applied' } elseif ($work.Contains($patch.find)) { 'pending' } else { 'ANCHOR-MISSING' }
        Write-Host ("patch {0,-20} {1}" -f $patch.id, $state)
    }
    exit 0
}

Copy-Item -LiteralPath $Target -Destination ("{0}.bak-{1}" -f $Target, (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force

# 1) reference block (replace in place when it already exists)
if ($hasBlock) {
    $pattern = [regex]::Escape($Begin) + '[\s\S]*?' + [regex]::Escape($End)
    $work = [regex]::Replace($work, $pattern, [System.Text.RegularExpressions.MatchEvaluator] { param($m) $wrapped })
    $applied += 'block:replaced'
} else {
    $work = $work.TrimEnd() + "`n`n" + $wrapped + "`n"
    $applied += 'block:appended'
}

# 2) targeted patches (skip when the marker is already there)
foreach ($patch in $spec.patches) {
    if ($work.Contains($patch.marker)) { $skipped += $patch.id; continue }
    if (-not $work.Contains($patch.find)) { $missing += $patch.id; continue }
    $work = $work.Replace($patch.find, $patch.replace)
    $applied += $patch.id
}

if ($work -ne $raw.Replace("`r`n", "`n")) {
    $out = if ($hadCrlf) { $work.Replace("`n", "`r`n") } else { $work }
    [System.IO.File]::WriteAllText($Target, $out, $utf8)
}

Write-Host ("target   : {0}" -f $Target)
Write-Host ("eol      : {0}" -f $(if ($hadCrlf) { 'CRLF' } else { 'LF' }))
Write-Host ("applied  : {0}" -f $(if ($applied) { $applied -join ', ' } else { '(nothing)' }))
Write-Host ("skipped  : {0}" -f $(if ($skipped) { $skipped -join ', ' } else { '(none)' }))
Write-Host ("missing  : {0}" -f $(if ($missing) { $missing -join ', ' } else { '(none)' }))
Write-Host ("bytes    : {0} -> {1}" -f $raw.Length, $work.Length)
if ($missing.Count -gt 0) { exit 2 }
