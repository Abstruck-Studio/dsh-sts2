param(
    [string]$GameDir = 'D:\.steam\steamapps\common\Slay the Spire 2',
    [string]$ModId = 'Sts2AgentBridge'
)
$ErrorActionPreference = 'Stop'
# csproj 里 AppendTargetFrameworkToOutputPath=false：输出直接在 bin\Release，没有 net9.0 子目录。
$src = Join-Path $PSScriptRoot 'bin\Release'
$dll = Join-Path $src "$ModId.dll"
if (-not (Test-Path $dll)) { throw "build output not found: $dll" }

$dest = Join-Path $GameDir "mods\$ModId"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $dll $dest -Force
Copy-Item (Join-Path $PSScriptRoot 'mod.json') $dest -Force
Write-Host "deployed -> $dest"
Get-ChildItem $dest | Select-Object Name, Length | Format-Table -AutoSize
