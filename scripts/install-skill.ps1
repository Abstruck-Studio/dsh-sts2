param(
    [string]$Target = (Join-Path $env:USERPROFILE '.dsh\skills\slay-the-spire-2\SKILL.md'),
    [switch]$Check
)
$ErrorActionPreference = 'Stop'

# Kept for compatibility: this used to only append the reference block.
# The real work now lives in apply-skill-updates.ps1 (block + the seven targeted patches).
$real = Join-Path $PSScriptRoot 'apply-skill-updates.ps1'
if (-not (Test-Path -LiteralPath $real)) { throw "missing: $real" }

$args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $real, '-Target', $Target)
if ($Check) { $args += '-Check' }
& powershell @args
exit $LASTEXITCODE
