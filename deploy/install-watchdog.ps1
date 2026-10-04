<#
.SYNOPSIS
    Installs BRMES on a single shop-floor machine: publish, run-as-service (optional), watchdog schedule.

.DESCRIPTION
    Two supervision layers on purpose:
      * Windows service  - starts at boot before anyone logs in, and the SCM restarts it on crash.
      * watchdog task    - runs every minute and probes /health, which also catches "process alive,
                           engine wedged" - something the SCM cannot see.
    If you only want the scheduled-task flavour, leave -ServiceName empty and the app is started by the
    watchdog with -Urls instead.

    Nothing here is executed against your machine unless you drop -WhatIf; the script declares
    SupportsShouldProcess, so a dry run prints exactly what would change.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Empty = the folder above this script, resolved in the body below: $PSScriptRoot is not reliably
    # set inside a parameter default on Windows PowerShell 5.1 (measured: Split-Path throws on an
    # empty Path when the script is started with `powershell.exe -File deploy\install-watchdog.ps1`).
    [string] $Repository = '',
    [string] $PublishTo = 'C:\brmes',
    [string] [Alias('BindUrl')] $Urls = 'http://localhost:5010',
    [string] $HealthUrl = 'http://localhost:5010/health',
    # Empty means "no Windows service": the watchdog starts the exe directly.
    [string] $ServiceName = 'BRMES',
    [string] $TaskName = 'BRMES-Watchdog',
    # Run the watchdog as this user (needs a password for a non-interactive task). Default: current user.
    [string] $RunAs = "$env:USERDOMAIN\$env:USERNAME",
    [string] $RunAsPassword = '',
    # These land in appsettings.Production.json on the target box. Jwt:Key is mandatory outside Development.
    [string] $JwtKey = '',
    [string] $Sqlite = 'Data Source=App_Data/recipes.db',
    [int] $BackupKeep = 7,
    [string] $BackupAtUtc = '02:15',
    [switch] $SkipPublish,
    # Deliver from a package built by publish-package.ps1 instead of publishing on this machine.
    # This is the path a plant PC should take: it needs no SDK, and the build it runs is named in
    # package-manifest.json inside the zip.
    [string] $PackagePath = '',
    # With -PackagePath: delete everything in the app directory except App_Data and logs before
    # unpacking. Off by default because it removes files; turn it on for a version-changing upgrade.
    [switch] $Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step { param([string] $Message) Write-Host "==> $Message" }

# Where this script itself lives (the repo's deploy folder). Same reason as -Repository: resolve in
# the body, not in a param default.
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
if (-not $Repository) { $Repository = Split-Path -Parent $scriptRoot }

$exePath = Join-Path $PublishTo 'RecipesManage.Api.exe'
$watchdogDst = Join-Path $PublishTo 'watchdog.ps1'
$logPath = Join-Path $PublishTo 'logs\watchdog.log'

if ($PackagePath -and -not (Test-Path $PackagePath)) { throw "no package at $PackagePath" }
if (-not $PackagePath) {
    # Without a package the watchdog script is copied straight from the repo (developer box flavour).
    $watchdogSrc = Join-Path $scriptRoot 'watchdog.ps1'
    if (-not (Test-Path $watchdogSrc)) { throw "missing $watchdogSrc" }
}

# ---- 1. get the binaries onto this machine ----
if ($PackagePath) {
    Step "unpack $PackagePath -> $PublishTo"
    if ($PSCmdlet.ShouldProcess($PublishTo, 'expand package')) {
        New-Item -ItemType Directory -Path $PublishTo -Force | Out-Null
        if ($Clean) {
            # Overlaying a new build leaves orphaned files from the old one behind (native deps and
            # appsettings sections are the usual ones), so an upgrade can ask for a clean directory.
            # Deliberately opt-in: this deletes files, and App_Data (the database) is never touched.
            Get-ChildItem -Path $PublishTo -Force |
                Where-Object { $_.Name -notin @('App_Data', 'logs') } |
                ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
        }
        Expand-Archive -Path $PackagePath -DestinationPath $PublishTo -Force
        $manifest = Join-Path $PublishTo 'package-manifest.json'
        if (Test-Path $manifest) { Get-Content $manifest -Raw | Write-Host }
    }
    # watchdog.ps1 travels inside the package, so the target never needs the repo.
    $watchdogSrc = Join-Path $PublishTo 'watchdog.ps1'
} elseif (-not $SkipPublish) {
    $apiProject = Join-Path $Repository 'src\RecipesManage.Api\RecipesManage.Api.csproj'
    if (-not (Test-Path $apiProject)) { throw "no project at $apiProject (pass -Repository)" }
    # The UI compiles into the API's wwwroot (frontend/vite.config.ts) and must exist before
    # dotnet publish, or this path installs an API with no UI. Prefer -PackagePath in the field.
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw "npm not on PATH: needed to build the UI into the publish output (or use -PackagePath with a package from publish-package.ps1)"
    }
    $frontend = Join-Path $Repository 'frontend'
    Step "build frontend -> src\RecipesManage.Api\wwwroot"
    if ($PSCmdlet.ShouldProcess($frontend, 'npm ci; npm run build')) {
        Push-Location $frontend
        try {
            & npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with $LASTEXITCODE" }
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw "npm run build failed with $LASTEXITCODE" }
        } finally { Pop-Location }
    }
    Step "publish $apiProject -> $PublishTo"
    if ($PSCmdlet.ShouldProcess($PublishTo, 'dotnet publish')) {
        # Self-contained is the point: the plant PC must not need an SDK matching our build machine.
        & dotnet publish $apiProject -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:VersionSuffix='' -o $PublishTo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE" }
    }
    $watchdogSrc = Join-Path $scriptRoot 'watchdog.ps1'
} else {
    $watchdogSrc = Join-Path $scriptRoot 'watchdog.ps1'
}

if ($PSCmdlet.ShouldProcess($PublishTo, 'copy watchdog')) {
    New-Item -ItemType Directory -Path $PublishTo -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $logPath) -Force | Out-Null
    if (-not (Test-Path $watchdogSrc)) {
        # Only reachable when the zip was built without watchdog.ps1 in it.
        throw "no watchdog script to install (expected $watchdogSrc)"
    }
    if ((Resolve-Path $watchdogSrc).Path -ne (Split-Path -Parent $watchdogDst)) {
        Copy-Item $watchdogSrc $watchdogDst -Force
    }
}

# ---- 2. runtime config on the target ----
if ($JwtKey -or $Sqlite) {
    Step 'write appsettings.Production.json'
    if ($PSCmdlet.ShouldProcess((Join-Path $PublishTo 'appsettings.Production.json'), 'write')) {
        # Non-Development startup refuses to run without a real key (no placeholder fallback), so this
        # file is what turns "works on my machine" into a machine that survives a reboot.
        $config = [ordered]@{
            ConnectionStrings = [ordered]@{ Sqlite = $Sqlite }
            Jwt               = [ordered]@{
                Issuer      = 'RecipesManage'
                Audience    = 'RecipesManage'
                Key         = $JwtKey
                ExpireHours = 12
            }
            Backup            = [ordered]@{
                Enabled   = $true
                AtUtc     = $BackupAtUtc
                Keep      = $BackupKeep
                Directory = 'App_Data/backups'
            }
            # Runs right after the daily backup: PRAGMA optimize always, VACUUM only when no batch is
            # in flight and the free pages are worth rewriting the whole file for.
            Maintenance       = [ordered]@{
                Enabled          = $true
                Vacuum           = $true
                MinFreeRatio     = 0.2
                MinFreeMegabytes = 64
                BusyTimeoutMs    = 30000
            }
            Logging           = [ordered]@{ LogLevel = [ordered]@{ Default = 'Information' } }
        }
        $config | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $PublishTo 'appsettings.Production.json') -Encoding UTF8
        Write-Host '   Jwt:Key is stored in plain text here. If this PC is shared, restrict the file ACL'
        Write-Host '   (or pass it through the environment variable Jwt__Key and delete it from this file).'
    }
}

# ---- 3. Windows service ----
if ($ServiceName) {
    Step "register service '$ServiceName'"
    if ($PSCmdlet.ShouldProcess($ServiceName, 'sc.exe create')) {
        # --contentRoot pins App_Data next to the binaries instead of whatever cwd the SCM hands over.
        $binPath = "`"$exePath`" --urls $Urls --contentRoot `"$PublishTo`""
        & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= 'BRMES batch recipe management'
        if ($LASTEXITCODE -eq 1073) {
            # Already installed (e.g. re-running this script): point it at the new path instead of failing.
            & sc.exe config $ServiceName binPath= $binPath
        } elseif ($LASTEXITCODE -ne 0) {
            throw "sc create failed with $LASTEXITCODE (run this script elevated)"
        }
        # Three fast retries then stay stopped: a plant PC should end up loudly down, not silently flapping.
        & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/600000
        # Without ASPNETCORE_ENVIRONMENT the app is Production -> Jwt:Key is enforced. That is intended.
        & sc.exe failureflag $ServiceName 1
    }
    Step "set BRMES_WINDOWS_SERVICE=1 + ASPNETCORE_ENVIRONMENT=Production for the service"
    if ($PSCmdlet.ShouldProcess($ServiceName, 'reg add environment')) {
        $key = "HKLM\SYSTEM\CurrentControlSet\Services\$ServiceName\Environment"
        foreach ($pair in @(
            'BRMES_WINDOWS_SERVICE=1',
            'ASPNETCORE_ENVIRONMENT=Production',
            "ASPNETCORE_URLS=$Urls")) {
            reg add $key /f /v ($pair.Split('=')[0]) /t REG_SZ /d ($pair.Substring($pair.IndexOf('=') + 1)) | Out-Null
        }
    }
    Step "start $ServiceName"
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Start-Service')) {
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }
}

# ---- 4. scheduled watchdog ----
Step "schedule $TaskName every 1 minute"
if ($PSCmdlet.ShouldProcess($TaskName, 'schtasks create')) {
    $psArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$watchdogDst`" -HealthUrl `"$HealthUrl`" " +
              "-Exe `"$exePath`" -AppDirectory `"$PublishTo`" -ContentRoot `"$PublishTo`" -Urls `"$Urls`" " +
              "-LogPath `"$logPath`" -ServiceName `"$ServiceName`""
    $action = "powershell.exe $psArgs"
    # /ru without a password means interactive-only; pass -RunAsPassword for a real headless plant PC.
    $schtasksArgs = @('/create', '/tn', $TaskName, '/sc', 'minute', '/mo', '1', '/f', '/tr', $action)
    if ($RunAsPassword) {
        $schtasksArgs += @('/ru', $RunAs, '/rp', $RunAsPassword)
    }
    & schtasks.exe @schtasksArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "schtasks failed with $LASTEXITCODE - register the task manually or elevated" }
}

Step 'done'
Write-Host @"

Next checks (see docs\deployment.md):
  1. curl $HealthUrl                     -> expect status ok with a pid
  2. start a second instance by hand      -> expect it to exit with code 75 and the lock holder named
  3. schtasks /run /tn $TaskName         -> writes nothing to $logPath when healthy (that is correct)
  4. taskkill /im RecipesManage.Api.exe  -> the watchdog must bring it back within ~1 minute
"@
