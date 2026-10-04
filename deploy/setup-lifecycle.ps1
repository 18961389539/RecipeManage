<#
.SYNOPSIS
    Transactional Windows service lifecycle for the BRMES Inno Setup package.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Prepare', 'Commit', 'Rollback', 'Uninstall')]
    [string] $Action,
    [Parameter(Mandatory = $true)]
    [string] $InstallRoot,
    [Parameter(Mandatory = $true)]
    [string] $DataRoot,
    [string] $Urls = 'http://localhost:5010',
    [switch] $DeleteData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Urls -notmatch '^https?://[A-Za-z0-9.\-\[\]:]+(?::\d+)?/?$') {
    throw "Invalid service URL '$Urls'. Use an HTTP(S) origin such as http://localhost:5010."
}
$Urls = $Urls.TrimEnd('/')

$serviceName = 'BRMES'
$taskName = 'BRMES-Watchdog'
$binaryRoot = Join-Path $InstallRoot 'bin'
$exePath = Join-Path $binaryRoot 'RecipesManage.Api.exe'
$watchdogPath = Join-Path $binaryRoot 'watchdog.ps1'
$stateRoot = Join-Path $DataRoot 'installer-state'
$statePath = Join-Path $stateRoot 'rollback.json'
$backupRoot = Join-Path $stateRoot 'rollback'
$productionConfig = Join-Path $DataRoot 'appsettings.Production.json'
$baseConfig = Join-Path $DataRoot 'appsettings.json'
$probeBase = $Urls -replace '://(0\.0\.0\.0|\*|\[?::\]?)', '://localhost'
$healthUrl = $probeBase + '/health'

function Invoke-Native {
    param([string] $File, [string[]] $Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$File failed with exit code $LASTEXITCODE"
    }
}

function Get-ServiceExists {
    return [bool](Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
}

function Stop-Brmes {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Disable-ScheduledTask -TaskName $taskName -ErrorAction Stop | Out-Null
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 500
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        } while ($task -and $task.State -eq 'Running' -and (Get-Date) -lt $deadline)
        if ($task -and $task.State -eq 'Running') {
            throw 'The BRMES watchdog is still running; retry after it exits.'
        }
    }
    if (Get-ServiceExists) {
        $service = Get-Service -Name $serviceName
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    }
}

function Set-ServiceRegistration {
    $quotedExe = '"' + $exePath + '"'
    $binPath = "$quotedExe --urls $Urls --contentRoot `"$DataRoot`" --webroot `"$binaryRoot\wwwroot`""
    if (Get-ServiceExists) {
        Invoke-Native 'sc.exe' @('config', $serviceName, 'binPath=', $binPath, 'start=', 'auto', 'DisplayName=', 'BRMES batch recipe management')
    } else {
        Invoke-Native 'sc.exe' @('create', $serviceName, 'binPath=', $binPath, 'start=', 'auto', 'DisplayName=', 'BRMES batch recipe management')
    }
    Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/600000')
    Invoke-Native 'sc.exe' @('failureflag', $serviceName, '1')

    $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    New-Item -Path $serviceKey -Force | Out-Null
    New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Force -Value @(
        'BRMES_WINDOWS_SERVICE=1',
        'ASPNETCORE_ENVIRONMENT=Production',
        "ASPNETCORE_URLS=$Urls"
    ) | Out-Null
}

function Set-WatchdogTask {
    $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$watchdogPath`" " +
        "-HealthUrl `"$healthUrl`" -Exe `"$exePath`" -AppDirectory `"$DataRoot`" " +
        "-ContentRoot `"$DataRoot`" -Urls `"$Urls`" -LogPath `"$DataRoot\logs\watchdog.log`" " +
        "-ServiceName `"$serviceName`""
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    $taskRun = "`"$powerShell`" $taskArgs"
    Invoke-Native 'schtasks.exe' @(
        '/create', '/tn', $taskName, '/sc', 'minute', '/mo', '1',
        '/ru', 'SYSTEM', '/rl', 'HIGHEST', '/f', '/tr', $taskRun
    )
}

function New-ProductionConfig {
    if (Test-Path $productionConfig) {
        $existing = Get-Content $productionConfig -Raw | ConvertFrom-Json
        $key = [string]$existing.Jwt.Key
        if ($key -and $key -notlike 'CHANGE-ME*' -and [Text.Encoding]::UTF8.GetByteCount($key) -ge 32) {
            return
        }
    }

    $template = Join-Path $binaryRoot 'appsettings.Production.json'
    if (Test-Path $productionConfig) {
        $config = Get-Content $productionConfig -Raw | ConvertFrom-Json
    } elseif (Test-Path $template) {
        $config = Get-Content $template -Raw | ConvertFrom-Json
    } else {
        $config = [pscustomobject]@{
            Seed = [pscustomobject]@{ Demo = $false; AdminPassword = '' }
            Jwt = [pscustomobject]@{ Key = ''; ExpireHours = 8 }
            Backup = [pscustomobject]@{ Enabled = $true; AtUtc = '02:15'; Keep = 7; KeepPreMigration = 3; Directory = 'App_Data/backups' }
            Logging = [pscustomobject]@{ File = [pscustomobject]@{ Enabled = $true; Directory = 'App_Data/logs'; MinimumLevel = 'Information'; RetainedDays = 0 } }
            Maintenance = [pscustomobject]@{ Enabled = $true }
        }
    }

    $bytes = New-Object byte[] 48
    $rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $config.Jwt.Key = [Convert]::ToBase64String($bytes)
    $config | ConvertTo-Json -Depth 12 | Set-Content -Path $productionConfig -Encoding UTF8
}

function Secure-DataDirectory {
    & icacls.exe $DataRoot /inheritance:r /grant:r `
        '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Could not restrict access to BRMES production data' }
}

function Wait-ForHealth {
    $until = (Get-Date).AddSeconds(60)
    do {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) {
                $body = $response.Content | ConvertFrom-Json
                if ($body.status -eq 'ok') { return }
            }
        } catch { }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $until)
    throw "BRMES did not become healthy at $healthUrl"
}

function Restore-FromState {
    if (-not (Test-Path $statePath)) { return }
    $state = Get-Content $statePath -Raw | ConvertFrom-Json
    Stop-Brmes
    if (Test-Path $binaryRoot) { Remove-Item $binaryRoot -Recurse -Force }
    if ($state.HadBinaries -and (Test-Path (Join-Path $backupRoot 'bin'))) {
        Copy-Item (Join-Path $backupRoot 'bin') $binaryRoot -Recurse -Force
    }
    foreach ($name in @('appsettings.json', 'appsettings.Production.json')) {
        $target = Join-Path $DataRoot $name
        $saved = Join-Path $backupRoot $name
        if ($state.Configs.$name) {
            Copy-Item $saved $target -Force
        } elseif (Test-Path $target) {
            Remove-Item $target -Force
        }
    }

    if ($state.HadService -and $state.ServiceImagePath) {
        $startType = switch ([int]$state.ServiceStart) {
            2 { 'auto' }
            3 { 'demand' }
            4 { 'disabled' }
            default { '' }
        }
        $serviceArgs = @('config', $serviceName, 'binPath=', [string]$state.ServiceImagePath)
        if ($startType) { $serviceArgs += @('start=', $startType) }
        if ($state.ServiceDisplayName) {
            $serviceArgs += @('DisplayName=', [string]$state.ServiceDisplayName)
        }
        Invoke-Native 'sc.exe' $serviceArgs
        $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
        if ($null -ne $state.ServiceFailureActions) {
            New-ItemProperty -Path $serviceKey -Name FailureActions -PropertyType Binary `
                -Force -Value ([byte[]]$state.ServiceFailureActions) | Out-Null
        } else {
            Remove-ItemProperty -Path $serviceKey -Name FailureActions -ErrorAction SilentlyContinue
        }
        if ($null -ne $state.ServiceFailureActionsFlag) {
            New-ItemProperty -Path $serviceKey -Name FailureActionsFlag -PropertyType DWord `
                -Force -Value ([int]$state.ServiceFailureActionsFlag) | Out-Null
        } else {
            Remove-ItemProperty -Path $serviceKey -Name FailureActionsFlag -ErrorAction SilentlyContinue
        }
        if ($state.ServiceEnvironment) {
            New-ItemProperty -Path $serviceKey `
                -Name Environment -PropertyType MultiString -Force -Value @($state.ServiceEnvironment) | Out-Null
        } else {
            Remove-ItemProperty -Path $serviceKey -Name Environment -ErrorAction SilentlyContinue
        }
        if ($state.ServiceWasRunning) {
            Start-Service -Name $serviceName
        }
    } elseif (Get-ServiceExists) {
        Invoke-Native 'sc.exe' @('delete', $serviceName)
    }
    $currentTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($currentTask) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    if ($state.HadTask -and $state.TaskXml) {
        Register-ScheduledTask -TaskName $taskName -Xml $state.TaskXml -Force | Out-Null
        if (-not $state.TaskEnabled) { Disable-ScheduledTask -TaskName $taskName | Out-Null }
    }
    Remove-Item $stateRoot -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    switch ($Action) {
        'Prepare' {
            New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
            New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
            if (Test-Path $backupRoot) { Remove-Item $backupRoot -Recurse -Force }
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $serviceRegistry = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -ErrorAction SilentlyContinue
            $taskXml = if ($task) { Export-ScheduledTask -TaskName $taskName -ErrorAction Stop } else { '' }
            $state = [ordered]@{
                HadBinaries = Test-Path $binaryRoot
                HadService = [bool]$service
                HadTask = [bool]$task
                TaskEnabled = [bool]($task -and $task.State -ne 'Disabled')
                TaskXml = $taskXml
                ServiceImagePath = if ($serviceRegistry) { [string]$serviceRegistry.ImagePath } else { '' }
                ServiceStart = if ($serviceRegistry) { [int]$serviceRegistry.Start } else { 0 }
                ServiceDisplayName = if ($serviceRegistry) { [string]$serviceRegistry.DisplayName } else { '' }
                ServiceWasRunning = [bool]($service -and $service.Status -eq 'Running')
                ServiceFailureActions = if ($serviceRegistry -and $serviceRegistry.PSObject.Properties['FailureActions']) {
                    [byte[]]$serviceRegistry.FailureActions
                } else { $null }
                ServiceFailureActionsFlag = if ($serviceRegistry) { $serviceRegistry.FailureActionsFlag } else { $null }
                ServiceEnvironment = if ($serviceRegistry) { @($serviceRegistry.Environment) } else { @() }
                Configs = [ordered]@{
                    'appsettings.json' = Test-Path $baseConfig
                    'appsettings.Production.json' = Test-Path $productionConfig
                }
            }
            if ($state.HadBinaries) { Copy-Item $binaryRoot (Join-Path $backupRoot 'bin') -Recurse -Force }
            foreach ($name in @('appsettings.json', 'appsettings.Production.json')) {
                if ($state.Configs.$name) { Copy-Item (Join-Path $DataRoot $name) (Join-Path $backupRoot $name) -Force }
            }
            $state | ConvertTo-Json -Depth 5 | Set-Content $statePath -Encoding UTF8
            Stop-Brmes
            if (Get-ServiceExists) {
                $deadline = (Get-Date).AddSeconds(30)
                while ((Get-Service -Name $serviceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 500
                }
                if ((Get-Service -Name $serviceName).Status -ne 'Stopped') {
                    throw 'BRMES service did not stop; no program files were changed.'
                }
            }
            if (Test-Path $binaryRoot) { Remove-Item $binaryRoot -Recurse -Force }
            New-Item -ItemType Directory -Path $binaryRoot -Force | Out-Null

            # Import a previous ZIP/PowerShell deployment without replacing existing data.
            if ($serviceRegistry -and $state.ServiceImagePath) {
                $rootMatch = [regex]::Match([string]$state.ServiceImagePath, '--contentRoot\s+"([^"]+)"')
                if ($rootMatch.Success) {
                    $legacyRoot = $rootMatch.Groups[1].Value
                } else {
                    $exeMatch = [regex]::Match([string]$state.ServiceImagePath, '^"([^"]+)"')
                    $legacyRoot = if ($exeMatch.Success) { Split-Path -Parent $exeMatch.Groups[1].Value } else { '' }
                }
                if ($legacyRoot -and (Test-Path $legacyRoot) -and ($legacyRoot -ne $DataRoot)) {
                    if (-not (Test-Path (Join-Path $DataRoot 'App_Data\recipes.db'))) {
                        $legacyData = Join-Path $legacyRoot 'App_Data'
                        if (Test-Path $legacyData) {
                            Get-ChildItem $legacyData -File -Recurse | ForEach-Object {
                                $relative = $_.FullName.Substring($legacyData.Length).TrimStart('\')
                                $destination = Join-Path (Join-Path $DataRoot 'App_Data') $relative
                                if (-not (Test-Path $destination)) {
                                    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                                    Copy-Item $_.FullName $destination
                                }
                            }
                        }
                    }
                    if (-not (Test-Path $productionConfig) -and (Test-Path (Join-Path $legacyRoot 'appsettings.Production.json'))) {
                        Copy-Item (Join-Path $legacyRoot 'appsettings.Production.json') $productionConfig
                    }
                }
            }
        }
        'Commit' {
            New-Item -ItemType Directory -Path (Join-Path $DataRoot 'App_Data') -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $DataRoot 'logs') -Force | Out-Null
            if (-not (Test-Path $baseConfig)) {
                Copy-Item (Join-Path $binaryRoot 'appsettings.json') $baseConfig -Force
            }
            New-ProductionConfig
            Secure-DataDirectory
            Set-ServiceRegistration
            Set-WatchdogTask
            Start-Service -Name $serviceName -ErrorAction Stop
            (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
            Wait-ForHealth
            Enable-ScheduledTask -TaskName $taskName | Out-Null
            Remove-Item $stateRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        'Rollback' {
            Restore-FromState
        }
        'Uninstall' {
            Stop-Brmes
            if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            }
            if (Get-ServiceExists) {
                Invoke-Native 'sc.exe' @('delete', $serviceName)
                Start-Sleep -Seconds 2
                if (Get-ServiceExists) { throw 'Service BRMES is marked for deletion; retry uninstall after it stops.' }
            }
            if ($DeleteData -and (Test-Path $DataRoot)) {
                Remove-Item $DataRoot -Recurse -Force
            }
        }
    }
    exit 0
} catch {
    Write-Error $_
    if ($Action -eq 'Commit') {
        try { Restore-FromState } catch { Write-Error "Rollback failed: $_" }
    }
    exit 1
}
