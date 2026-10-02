<#
.SYNOPSIS
    Builds the deployable package for a shop-floor machine, on the developer box.

.DESCRIPTION
    Why this exists: install-watchdog.ps1 used to run `dotnet publish` on the TARGET machine, which
    contradicts docs\deployment.md ("the plant PC does not need an SDK"). A plant PC should never have
    a build toolchain on it - it cannot restore packages when the network drops, and nobody can say
    which SDK version produced the thing that is running.

    So the split is: this script publishes here (self-contained win-x64), stamps the version and the
    commit into the assembly metadata, and writes one zip plus a manifest. On the target you run
    install-watchdog.ps1 -PackagePath <zip> and it only ever unpacks files.

    The stamped version is what /health reports, so "which build is that machine on" is answerable
    from a phone call instead of a site visit.

.EXAMPLE
    .\publish-package.ps1 -Version 1.0.1
    .\publish-package.ps1 -Version 1.0.1 -OutDir D:\shares\brmes
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Empty means "the folder that contains this script's parent" - resolved in the body, because
    # $PSScriptRoot is not reliably set inside a parameter default on Windows PowerShell 5.1
    # (measured: `Split-Path -Parent $PSScriptRoot` throws "Path is an empty string" there).
    [string] $Repository = '',
    # Leave empty to take <Version> from Directory.Build.props.
    [string] $Version = '',
    [string] $Runtime = 'win-x64',
    # 'true' (default) = self-contained: the plant PC needs no .NET at all. 'false' = framework
    # dependent, only for a machine that already has the matching runtime. A validated string rather
    # than a [switch], because `-SelfContained:$false` gets mangled when this script is invoked
    # through another shell - and a silently-mangled flag means the package is not what the operator
    # thinks it is.
    [ValidateSet('true', 'false')]
    [string] $SelfContained = 'true',
    [string] $OutDir = '',
    # Extra 'dotnet publish' properties, e.g. -Property @('PublishReadyToRun=true').
    [string[]] $Property = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step { param([string] $Message) Write-Host "==> $Message" }

# Resolve the repo root here instead of in the param default.
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
if (-not $Repository) { $Repository = Split-Path -Parent $scriptRoot }

try {
    $apiProject = Join-Path $Repository 'src\RecipesManage.Api\RecipesManage.Api.csproj'
    if (-not (Test-Path $apiProject)) { throw "no project at $apiProject (pass -Repository)" }

    if (-not $Version) {
        # Single source of truth: the same props file MSBuild reads.
        $props = Join-Path $Repository 'Directory.Build.props'
        $Version = (Select-String -Path $props -Pattern '<Version>(.+?)</Version>' |
                    Select-Object -First 1).Matches[0].Groups[1].Value
        if (-not $Version) { throw "no <Version> in $props and -Version was empty" }
    }

    $commit = ''
    Push-Location $Repository
    try {
        $commit = (& git rev-parse HEAD 2>$null) -join ''
        if ($LASTEXITCODE -ne 0) { $commit = '' }
    } finally { Pop-Location }
    if ($commit) {
        $commit = $commit.Trim()
        if ($commit.Length -gt 40) { $commit = $commit.Substring(0, 40) }
    }

    if (-not $OutDir) { $OutDir = Join-Path $Repository 'artifacts' }
    # Short sha in the label: the full hash is in package-manifest.json, and a phone call can carry eight.
    $label = if ($commit.Length -ge 8) { "$Version+$($commit.Substring(0,8))" } else { $Version }
    $safeLabel = $label -replace '[^0-9A-Za-z.+_-]', '-'
    $staging = Join-Path $OutDir "brmes-$safeLabel"
    $zipPath = Join-Path $OutDir "brmes-$safeLabel-$Runtime.zip"

    Step "publish $Runtime v$label -> $staging"
    if ($PSCmdlet.ShouldProcess($staging, 'dotnet publish')) {
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        $publishArgs = @('publish', $apiProject, '-c', 'Release', '-r', $Runtime,
                         '--self-contained', $SelfContained,
                         '-p:PublishSingleFile=false',
                         "-p:Version=$Version",
                         "-p:InformationalVersion=$label",
                         # The label already carries the short commit; without this the SDK appends the
                         # full 40-char hash after it and /health reports an unquotable version.
                         '-p:IncludeSourceRevisionInInformationalVersion=false',
                         '-o', $staging)
        if ($Property) { $publishArgs += ($Property | ForEach-Object { "-p:$_" }) }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE" }

        # The watchdog script travels inside the package: the target machine has no repo to copy it from.
        Copy-Item (Join-Path $scriptRoot 'watchdog.ps1') (Join-Path $staging 'watchdog.ps1') -Force

        $manifest = [ordered]@{
            product       = 'BRMES'
            version       = $Version
            informational = $label
            commit        = $commit
            runtime       = $Runtime
            selfContained = [bool]::Parse($SelfContained)
            builtAtUtc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            machine       = "$env:COMPUTERNAME"
        }
        $manifest | ConvertTo-Json -Depth 4 |
            Set-Content -Path (Join-Path $staging 'package-manifest.json') -Encoding UTF8
    }

    Step "zip -> $zipPath"
    if ($PSCmdlet.ShouldProcess($zipPath, 'compress')) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        # Compress-Archive needs the folder's contents; -Path staging\* keeps the zip root flat.
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
        "$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -Path "$zipPath.sha256" -Encoding ASCII
        Write-Host "   sha256 $hash"
        Write-Host "   Install on the plant PC with:  install-watchdog.ps1 -PackagePath `"$zipPath`""
    }
} catch {
    # A packaging script that reports success while broken is worse than one that fails: the operator
    # carries a half-built package to a plant PC and finds out there. Exit 2 = "the script itself broke".
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 2
}
