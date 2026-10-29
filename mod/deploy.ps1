param(
    [string]$GameDir = 'D:\.steam\steamapps\common\Slay the Spire 2',
    [string]$ModId = 'Sts2AgentBridge'
)
$ErrorActionPreference = 'Stop'

# ASCII-only on purpose: Windows PowerShell 5.1 decodes a BOM-less .ps1 as ANSI, and a CJK
# comment can then swallow the following line (a trailing lead byte eats the CRLF).

# csproj sets AppendTargetFrameworkToOutputPath=false: output goes straight to bin\Release.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$src = Join-Path $root 'bin\Release'
$dll = Join-Path $src "$ModId.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "build output not found: $dll (run: dotnet build -c Release)" }

# The mod loader reads EVERY *.json in the mod folder as a manifest, so two manifests carrying the
# same id make it report "multiple mods with id X". Only "<id>.json" is the real manifest;
# "mod.json" was an older camelCase draft and must never be deployed.
$manifest = Join-Path $root "$ModId.json"
if (-not (Test-Path -LiteralPath $manifest)) { throw "manifest not found: $manifest" }

# The DLL is loaded into the running game and therefore locked: fail loudly instead of half-copying.
$procs = @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    throw "game is running (pid $($procs[0].Id)): the DLL is locked. Close the game, then re-run."
}

$dest = Join-Path $GameDir "mods\$ModId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Self-heal: drop the legacy duplicate manifest if an earlier deploy copied it.
$legacy = Join-Path $dest 'mod.json'
if (Test-Path -LiteralPath $legacy) {
    Remove-Item -LiteralPath $legacy -Force
    Write-Host "      removed legacy duplicate manifest -> $legacy"
}

Copy-Item -LiteralPath $dll -Destination (Join-Path $dest "$ModId.dll") -Force
Copy-Item -LiteralPath $manifest -Destination (Join-Path $dest "$ModId.json") -Force

Write-Host "deployed -> $dest"
Get-ChildItem $dest | Select-Object Name, Length | Format-Table -AutoSize
