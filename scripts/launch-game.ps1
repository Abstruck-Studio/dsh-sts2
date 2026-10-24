#Requires -Version 5.1
<#
    launch-game.ps1 - start Slay the Spire 2 THROUGH STEAM and wait for the agent bridge.

    Launching the game exe directly breaks Steamworks init, so this script always goes through
    `steam.exe -applaunch 2868840`. If the game is already running it does not launch again:
    it just polls the bridge until it is ready (or the timeout expires).

    Usage:
        .\launch-game.ps1
        .\launch-game.ps1 -Deploy
        .\launch-game.ps1 -TimeoutSeconds 180

    Exit codes:
        0  bridge ready   (prints: OK bridge=...)
        1  failed/timeout (prints: FAIL + process state + last /health answer)
#>
[CmdletBinding()]
param(
    # Copy ..\mod\bin\Release\Sts2AgentBridge.dll into the game's mods folder before launching.
    [switch]$Deploy,

    [string]$GameDir = 'D:\.steam\steamapps\common\Slay the Spire 2',
    [string]$SteamExe = 'D:\.steam\steam.exe',
    [string]$AppId = '2868840',
    [string]$BridgeUrl = 'http://127.0.0.1:47821',

    [ValidateRange(5, 1800)]
    [int]$TimeoutSeconds = 120,

    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 3
)

$ErrorActionPreference = 'Stop'

$ProcessName = 'SlayTheSpire2'
$ModId = 'Sts2AgentBridge'
$HealthUrl = ($BridgeUrl.TrimEnd('/')) + '/health'
$ModDll = Join-Path $GameDir ("mods\$ModId\$ModId.dll")

function Get-GameProcesses {
    @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
}

# Returns @{ Ready = [bool]; Detail = [string] }. Never throws: a refused connection is an
# expected state while the game is still booting.
function Test-BridgeReady {
    try {
        $resp = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 2
        $raw = [string]$resp.Content
    }
    catch {
        return @{ Ready = $false; Detail = 'health unreachable: ' + $_.Exception.Message }
    }

    $obj = $null
    try { $obj = $raw | ConvertFrom-Json } catch { }

    if ($null -eq $obj) {
        return @{ Ready = $false; Detail = 'health returned non-JSON: ' + $raw.Trim() }
    }
    if ($obj.ok -eq $true -and $obj.attached -eq $true) {
        return @{ Ready = $true; Detail = $raw.Trim() }
    }
    return @{ Ready = $false; Detail = 'health not ready: ' + $raw.Trim() }
}

function Format-Pids {
    param([object[]]$Processes)
    # A function that found nothing returns $null, and @($null) is a 1-element array - filter it.
    $list = @($Processes | Where-Object { $null -ne $_ })
    if ($list.Count -eq 0) { return 'NOT running' }
    return 'running pid ' + (($list | ForEach-Object { $_.Id }) -join ',')
}

$procs = @(Get-GameProcesses)
$wasRunning = $procs.Count -gt 0

# ---------------------------------------------------------------- 1. optional deploy
if ($Deploy) {
    Write-Host '[deploy] source DLL -> game mods folder'
    if ($wasRunning) {
        # The mod DLL is loaded into the running game and therefore locked.
        Write-Warning "game is running (pid $(@($procs)[0].Id)): DLL is locked, deploy skipped. Close the game and re-run with -Deploy."
    }
    else {
        $source = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ("..\mod\bin\Release\$ModId.dll")))
        if (-not (Test-Path -LiteralPath $source)) {
            Write-Host "FAIL deploy source missing: $source (build the mod first: dotnet build -c Release)"
            exit 1
        }
        $destDir = Join-Path $GameDir "mods\$ModId"
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $destDir "$ModId.dll") -Force
        Write-Host "      deployed -> $destDir\$ModId.dll"
    }
}

# ---------------------------------------------------------------- 2. launch through Steam
if ($wasRunning) {
    Write-Host ("[launch] game already running (pid(s): {0}) - not launching again, waiting for the bridge" -f `
        (($procs | ForEach-Object { $_.Id }) -join ','))
}
else {
    Write-Host "[launch] steam.exe -applaunch $AppId"
    if (-not (Test-Path -LiteralPath $SteamExe)) {
        Write-Host "FAIL steam.exe not found: $SteamExe"
        exit 1
    }
    # -ArgumentList is passed as an array; Start-Process quotes each element.
    Start-Process -FilePath $SteamExe -ArgumentList '-applaunch', $AppId | Out-Null
    Write-Host '        launch requested (the game needs ~60s before the bridge answers)'
}

# ---------------------------------------------------------------- 3. poll the bridge
Write-Host "[wait] $HealthUrl  (timeout ${TimeoutSeconds}s, every ${IntervalSeconds}s)"

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$last = @{ Ready = $false; Detail = 'not polled yet' }
$ready = $false
$attempts = 0

while ((Get-Date) -lt $deadline) {
    $attempts++
    $last = Test-BridgeReady
    if ($last.Ready) { $ready = $true; break }
    Start-Sleep -Seconds $IntervalSeconds
}

$stopwatch.Stop()
$waited = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
$procs = @(Get-GameProcesses)

# ---------------------------------------------------------------- 4. report
if ($ready) {
    Write-Host ("OK bridge={0} attached=true waited={1}s attempts={2} game={3}" -f `
            $BridgeUrl, $waited, $attempts, (Format-Pids $procs))
    exit 0
}

Write-Host ("FAIL bridge not ready after {0}s ({1} attempts)" -f $waited, $attempts)
Write-Host ("      game process: {0}" -f (Format-Pids $procs))
Write-Host ("      last health:  {0}" -f $last.Detail)
if (-not (Test-Path -LiteralPath $ModDll)) {
    Write-Host "      hint: mod DLL missing -> $ModDll (run with -Deploy after building the mod)"
}
else {
    Write-Host '      hint: check the game log for a mod load error; a cold start can take up to ~90s'
}
exit 1
