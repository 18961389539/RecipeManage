<#
.SYNOPSIS
    BRMES single-machine watchdog: probe /health, repair if the app is down or wedged.

.DESCRIPTION
    Why a script and not just "restart on failure": the SCM can only see a process that exited.
    The failure that actually hurts on a shop-floor PC is "process alive, engine wedged" -
    a stuck PLC socket or a deadlocked loop keeps the port answering nothing while Task Manager
    still shows a healthy process. This script probes the HTTP endpoint, so it catches both.

    It also honours the single-instance lock: if the app refuses to start because another process
    owns the database (exit code 75), restarting in a loop is pointless and noisy - it logs and stops.

    Written in English on purpose: Windows PowerShell 5.1 reads UTF-8 files without a BOM as ANSI,
    which turns non-ASCII comments and log strings into mojibake on the target machine.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $HealthUrl = 'http://localhost:5010/health',
    # Full path of the published executable. Only processes started from exactly this file may be killed.
    [string] $Exe = 'C:\brmes\RecipesManage.Api.exe',
    [string] $AppDirectory = 'C:\brmes',
    # The default connection string is "Data Source=App_Data/recipes.db" - relative. Without pinning the
    # content root, a watchdog started from another cwd silently creates a *second* database next to itself.
    [string] $ContentRoot = '',
    [string] [Alias('BindUrl')] $Urls = 'http://localhost:5010',
    [string] $LogPath = 'C:\brmes\logs\watchdog.log',
    [string] $ServiceName = '',
    [string] [Alias('ProcName')] $ProcessName = 'RecipesManage.Api',
    [int] $TimeoutSec = 10,
    # Seconds to wait after a repair attempt before declaring it failed.
    [int] $SettleSec = 25,
    # Do not try again this soon after a restart, so a crash loop cannot hammer the PLC.
    [int] $RestartCooldownSec = 180,
    [switch] $VerboseHeartbeat
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ContentRoot) { $ContentRoot = $AppDirectory }

$statePath = Join-Path (Split-Path -Parent $LogPath) 'watchdog.state'
$exitHealthy = 0
$exitRepaired = 0
$exitFailed = 1

function Write-Log {
    param([string] $Level, [string] $Message)
    $line = '{0} {1,-7} {2}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    try {
        $dir = Split-Path -Parent $LogPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        if ((Test-Path $LogPath) -and ((Get-Item $LogPath).Length -gt 1MB)) {
            # Keep two generations at most; a watchdog log is read for forensics, not archived.
            Move-Item -Path $LogPath -Destination "$LogPath.1" -Force
        }
        Add-Content -Path $LogPath -Value $line -Encoding UTF8
    } catch {
        # Never let logging failure turn a working repair into a reported failure.
    }
}

function Get-Health {
    param([string] $Url, [int] $Timeout)
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $Timeout
        if ($response.StatusCode -ne 200) { return $null }
        $body = $response.Content | ConvertFrom-Json
        return [pscustomobject]@{
            Pid         = [int] $body.pid
            StartedAtUtc = [string] $body.startedAtUtc
            Database    = [string] $body.database
        }
    } catch {
        return $null
    }
}

function Get-OwnProcess {
    # Only processes started from the configured executable: a dev instance elsewhere must never be a kill target.
    return @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path -ieq $Exe) } catch { $false }
    })
}

function Test-InCooldown {
    if (-not (Test-Path $statePath)) { return $false }
    try {
        $last = (Get-Content $statePath -Raw).Trim()
        $lastRestart = [datetime]::ParseExact($last, 'o', $null)
        return ((Get-Date).ToUniversalTime() - $lastRestart).TotalSeconds -lt $RestartCooldownSec
    } catch {
        return $false
    }
}

function Set-LastRestart {
    try { (Get-Date).ToUniversalTime().ToString('o') | Set-Content -Path $statePath -Encoding ASCII } catch { }
}

function Invoke-Repair {
    # @(…) is load-bearing: PowerShell collapses an empty array returned from a function to $null,
    # and then `.Count` throws under Set-StrictMode - exactly in the "app is not running at all" case,
    # which is the one this script exists for.
    $procs = @(Get-OwnProcess)
    if ($procs.Count -gt 0) {
        # The process exists but does not answer: that is the wedged case, and the SCM would not notice it.
        if ($ServiceName -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
            Write-Log 'WARN' "service '$ServiceName' is installed but $($HealthUrl) did not answer; restarting the service"
            if ($PSCmdlet.ShouldProcess($ServiceName, 'Restart-Service')) {
                Restart-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                Set-LastRestart
                return
            }
        }
        Write-Log 'WARN' "$($procs.Count) process(es) match $Exe but health fails - stopping them before restart"
        if ($PSCmdlet.ShouldProcess($Exe, 'Stop wedged processes')) {
            $procs | ForEach-Object {
                try { $_.Kill(); $_.WaitForExit(10000) | Out-Null } catch { Write-Log 'WARN' "kill failed: $($_.Exception.Message)" }
            }
            Set-LastRestart
        }
    }

    if (-not (Test-Path $Exe)) {
        Write-Log 'ERROR' "executable not found: $Exe (fix -Exe or run install-watchdog.ps1)"
        return
    }

    Write-Log 'INFO' "starting $Exe --urls $Urls (contentRoot $ContentRoot)"
    if (-not $PSCmdlet.ShouldProcess($Exe, 'Start-Process')) { return }
    Set-LastRestart
    try {
        # -PassThru so the exit code is readable: 75 means "another instance owns the database".
        $child = Start-Process -FilePath $Exe -WorkingDirectory $AppDirectory `
            -ArgumentList @('--urls', $Urls, '--contentRoot', $ContentRoot) -WindowStyle Hidden -PassThru
    } catch {
        Write-Log 'ERROR' "Start-Process failed: $($_.Exception.Message)"
        return
    }

    $deadline = (Get-Date).AddSeconds($SettleSec)
    while ((Get-Date) -lt $deadline) {
        if ($child.HasExited) {
            if ($child.ExitCode -eq 75) {
                # This is the single-instance guard doing its job. Looping here would just spam the log:
                # somebody (or something) already drives this device and must be dealt with by a human.
                Write-Log 'ERROR' 'startup refused: another instance holds the database lock (exit 75). Not retrying until the cooldown passes.'
                return
            }
            Write-Log 'ERROR' "process exited early with code $($child.ExitCode)"
            return
        }
        Start-Sleep -Seconds 2
        $probe = Get-Health -Url $HealthUrl -Timeout $TimeoutSec
        if ($probe) {
            Write-Log 'INFO' "repaired: pid $($probe.Pid) started $($probe.StartedAtUtc)"
            return
        }
    }
    Write-Log 'ERROR' "started but $HealthUrl still does not answer after $SettleSec s"
}

# ---- main ----
# Any unexpected error must surface as a non-zero exit: Task Scheduler's "last result" is the only
# signal a headless plant PC gives anybody. Without this block an exception leaves $LASTEXITCODE at
# whatever the last external command returned (often 0), i.e. the watchdog reports "fine" while broken.
try {
    $health = Get-Health -Url $HealthUrl -Timeout $TimeoutSec
    if ($health) {
        if ($VerboseHeartbeat) {
            Write-Log 'INFO' "healthy: pid $($health.Pid) started $($health.StartedAtUtc) db=$($health.Database)"
        }
        exit $exitHealthy
    }

    Write-Log 'WARN' "$HealthUrl did not answer"
    if (Test-InCooldown) {
        Write-Log 'INFO' "last restart was less than $RestartCooldownSec s ago - waiting instead of restarting again"
        exit $exitFailed
    }
    Invoke-Repair

    $after = Get-Health -Url $HealthUrl -Timeout $TimeoutSec
    if ($after) {
        Write-Log 'INFO' "healthy after repair: pid $($after.Pid)"
        exit $exitRepaired
    }
    Write-Log 'ERROR' 'still unhealthy after repair attempt'
    exit $exitFailed
} catch {
    Write-Log 'ERROR' "watchdog itself failed: $($_.Exception.Message)"
    exit 2
}
