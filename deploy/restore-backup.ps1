<#
.SYNOPSIS
    Restores a verified database snapshot onto a shop-floor machine - the deployment doc's §8
    recovery drill, codified.

.DESCRIPTION
    Why this exists: the recovery drill (stop the app, move recipes.db together with its -wal/-shm
    sidecars aside, copy a brmes-<utc>.db snapshot over recipes.db, start again, check /health and a
    historical batch record) was a manual checklist. A checklist that is only ever read - never
    rehearsed - fails on the day it is needed. This script performs the same steps in order, keeps
    the old database in a quarantine folder (never deletes it), and rolls back automatically when
    the restored database does not come up healthy.

    What it does, in order:
      1. validates the snapshot (exists, non-empty, SQLite file header) and refuses to continue if
         the live database path is the snapshot itself;
      2. disables the watchdog task (so it cannot restart the app mid-swap), stops the service, and
         kills any RecipesManage.Api process whose exe lives under -PublishTo;
      3. moves <db> and <db>-wal / <db>-shm into <dbdir>\pre-restore-<utc>\;
      4. copies the snapshot in as <db> (copy to .restoring, then rename) and verifies the SHA256;
      5. starts the service, re-enables the watchdog, waits for /health;
      6. on a failed health check: stops again, reinstates the quarantined files, starts again, and
         fails the script with a message saying the previous database is back in place.

    The quarantine folder is kept after a successful restore so the operator can delete it once the
    new database has been checked. Use -NoStart to swap the files without starting anything (rehearsal
    / sandbox, or a maintenance window where the operator starts the app by hand).

    Exit codes: 0 = restored (or nothing to do under -WhatIf), 2 = failed (message says whether the
    previous database was reinstated). Requires an elevated session for service / task control.

.PARAMETER Backup
    Path to the snapshot to restore, e.g. C:\brmes\App_Data\backups\brmes-20261003-021500.db.

.PARAMETER PublishTo
    Application directory (where RecipesManage.Api.exe lives). Also the root for process matching.

.PARAMETER Database
    Database path, relative to -PublishTo unless rooted. Default: App_Data\recipes.db.

.PARAMETER ServiceName
    Windows service to stop/start. Empty string = do not touch any service
    (note: with `powershell -File`, empty strings are dropped - use `powershell -Command` or
    quote as `-ServiceName '""'`; plain `.\restore-backup.ps1 -ServiceName ''` in a session is fine).

.PARAMETER TaskName
    Watchdog scheduled task to disable/re-enable. Empty string = do not touch any task (same
    invocation note as -ServiceName).

.PARAMETER HealthUrl
    Liveness probe used to confirm the restored database comes up.

.PARAMETER NoStart
    Do not start the service / re-enable the watchdog / wait for health - the swap only.

.EXAMPLE
    # Rehearse against a scratch copy (no service, no task, nothing to start):
    .\deploy\restore-backup.ps1 -Backup .\snap.db -PublishTo D:\scratch -ServiceName '' -TaskName '' -NoStart

.EXAMPLE
    # Real restore on a plant PC, with a dry run first:
    .\deploy\restore-backup.ps1 -Backup C:\brmes\App_Data\backups\brmes-20261003-021500.db -PublishTo C:\brmes -WhatIf
    .\deploy\restore-backup.ps1 -Backup C:\brmes\App_Data\backups\brmes-20261003-021500.db -PublishTo C:\brmes
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string] $Backup,
    [string] $PublishTo = 'C:\brmes',
    [string] $Database = 'App_Data\recipes.db',
    [string] $ServiceName = 'BRMES',
    [string] $TaskName = 'BRMES-Watchdog',
    [string] $HealthUrl = 'http://localhost:5010/health',
    [int] $TimeoutSec = 60,
    [switch] $NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step { param([string] $Message) Write-Host "==> $Message" }

function Move-DatabaseAside {
    param([string] $Database, [string] $Quarantine)
    New-Item -ItemType Directory -Path $Quarantine -Force | Out-Null
    foreach ($suffix in @('', '-wal', '-shm')) {
        $file = "$Database$suffix"
        if (Test-Path -LiteralPath $file) { Move-Item -LiteralPath $file -Destination (Join-Path $Quarantine (Split-Path -Leaf $file)) }
    }
}

function Reinstate-Quarantined {
    param([string] $Database, [string] $Quarantine)
    foreach ($suffix in @('', '-wal', '-shm')) {
        $file = "$Database$suffix"
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        $saved = Join-Path $Quarantine (Split-Path -Leaf $file)
        if (Test-Path -LiteralPath $saved) { Move-Item -LiteralPath $saved -Destination $file }
    }
}

function Stop-App {
    param([string] $PublishTo, [string] $ServiceName, [string] $TaskName)
    # The watchdog restarts the app within a minute - disable it first, or it races the swap.
    if ($TaskName -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        if ((Get-ScheduledTask -TaskName $TaskName).State -ne 'Disabled') { Disable-ScheduledTask -TaskName $TaskName | Out-Null }
    }
    if ($ServiceName -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Stop-Service -Name $ServiceName -Force
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    # Only processes whose exe lives under -PublishTo: a developer's `dotnet run` elsewhere is not ours to kill.
    Get-Process -Name 'RecipesManage.Api' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($PublishTo, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

function Start-App {
    param([string] $ServiceName, [string] $TaskName)
    if ($TaskName -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Enable-ScheduledTask -TaskName $TaskName | Out-Null
    }
    if ($ServiceName -and (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        Start-Service -Name $ServiceName
    }
}

function Wait-Healthy {
    param([string] $HealthUrl, [int] $TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((& curl.exe -s -o NUL -w "%{http_code}" $HealthUrl) -eq '200') { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

try {
    # ---- validation (nothing is touched before these pass) ----
    if (-not (Test-Path -LiteralPath $Backup -PathType Leaf)) { throw "no snapshot at $Backup" }
    $backupFull = (Resolve-Path -LiteralPath $Backup).Path
    if ((Get-Item -LiteralPath $backupFull).Length -lt 16) { throw "$backupFull is too small to be a database" }
    $headerBytes = [IO.File]::ReadAllBytes($backupFull)[0..14]
    $header = [Text.Encoding]::ASCII.GetString($headerBytes)
    if ($header -ne 'SQLite format 3') { throw "$backupFull does not look like a SQLite database (header: '$header')" }

    if (-not [IO.Path]::IsPathRooted($Database)) { $Database = Join-Path $PublishTo $Database }
    $Database = [IO.Path]::GetFullPath($Database)
    $dbDir = Split-Path -Parent $Database
    if (-not (Test-Path -LiteralPath $dbDir)) { throw "database directory not found: $dbDir" }
    if ($Database -eq $backupFull) { throw 'the snapshot path equals the live database path - nothing to restore' }

    $quarantine = Join-Path $dbDir ("pre-restore-" + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))

    Step "restore $backupFull -> $Database"
    Write-Host "   previous database (if any) will be kept in $quarantine"

    if (-not $PSCmdlet.ShouldProcess($Database, "restore from $backupFull")) {
        Write-Host '   (no changes made)'
        exit 0
    }

    $hasService = $ServiceName -and [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
    $hasTask = $TaskName -and [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
    if (-not $NoStart -and -not $hasService -and -not $hasTask) {
        # Neither layer can bring the app back: waiting for /health would fail and trigger a pointless
        # rollback. Say so instead, and point at -NoStart (rehearsal) or a manual start.
        throw "no service '$ServiceName' and no task '$TaskName' to start the app; use -NoStart to swap files only, or start the app yourself"
    }

    Step 'stop the app (watchdog task disabled, service stopped, stray processes of this install killed)'
    Stop-App -PublishTo $PublishTo -ServiceName $ServiceName -TaskName $TaskName

    Step 'keep the previous database aside'
    Move-DatabaseAside -Database $Database -Quarantine $quarantine

    Step 'install the snapshot'
    $restoring = "$Database.restoring"
    Copy-Item -LiteralPath $backupFull -Destination $restoring -Force
    Move-Item -LiteralPath $restoring -Destination $Database -Force
    $expected = (Get-FileHash -LiteralPath $backupFull -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath $Database -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw "copied database differs from the snapshot (sha256 $actual vs $expected)" }

    if ($NoStart) {
        Step 'done (files swapped; -NoStart: the app was not started, /health not probed)'
        Write-Host "   previous database kept in $quarantine - delete it once you are satisfied"
        exit 0
    }

    Step 'start the app and wait for /health'
    Start-App -ServiceName $ServiceName -TaskName $TaskName
    if (Wait-Healthy -HealthUrl $HealthUrl -TimeoutSec $TimeoutSec) {
        Write-Host ''
        Write-Host "RESTORE OK - previous database kept in $quarantine"
        Write-Host '   check /health and open a historical batch record, then delete the quarantine folder.'
        exit 0
    }

    Step "health check failed within ${TimeoutSec}s - rolling the previous database back"
    Stop-App -PublishTo $PublishTo -ServiceName $ServiceName -TaskName $TaskName
    Reinstate-Quarantined -Database $Database -Quarantine $quarantine
    Start-App -ServiceName $ServiceName -TaskName $TaskName
    if (Wait-Healthy -HealthUrl $HealthUrl -TimeoutSec $TimeoutSec) {
        throw 'restore failed (the snapshot did not come up); the previous database is back in place and healthy'
    }
    throw 'restore failed AND rollback did not come up healthy - inspect the log under App_Data\logs and restore from a different snapshot'
} catch {
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 2
}