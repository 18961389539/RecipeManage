<#
.SYNOPSIS
    Builds one self-contained Windows installer containing the BRMES service and web UI.
#>
[CmdletBinding()]
param(
    [string] $Repository = '',
    [string] $Version = '',
    [string] $OutDir = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
if (-not $Repository) { $Repository = Split-Path -Parent $scriptRoot }
if (-not $OutDir) { $OutDir = Join-Path $Repository 'artifacts' }

if (-not $Version) {
    $props = Join-Path $Repository 'Directory.Build.props'
    $Version = (Select-String -Path $props -Pattern '<Version>(.+?)</Version>' |
        Select-Object -First 1).Matches[0].Groups[1].Value
}
if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') {
    throw "Invalid product version '$Version'. Use a numeric version such as 1.0.1."
}

$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) { $isccCommand.Source } else { '' }
if (-not $iscc) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 compiler not found. Install JRSoftware.InnoSetup and retry.' }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$publish = Join-Path $Repository 'deploy\publish-package.ps1'
$powerShell = (Get-Command powershell.exe -ErrorAction Stop).Source
$npm = (Get-Command npm.cmd -ErrorAction Stop).Source
$tempRoot = Join-Path $env:TEMP "brmes-installer-build-$PID"
$tempFrontend = Join-Path $tempRoot 'frontend'
$tempWebRoot = Join-Path $tempRoot 'src\RecipesManage.Api\wwwroot'
try {
    $frontend = Join-Path $Repository 'frontend'
    New-Item -ItemType Directory -Path $tempFrontend -Force | Out-Null
    & robocopy.exe $frontend $tempFrontend /E /XD (Join-Path $frontend 'node_modules') `
        /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Could not stage frontend sources (robocopy $LASTEXITCODE)" }

    Push-Location $tempFrontend
    try {
        & $npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with $LASTEXITCODE" }
        & $npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with $LASTEXITCODE" }
    } finally { Pop-Location }

    $repositoryWebRoot = Join-Path $Repository 'src\RecipesManage.Api\wwwroot'
    & robocopy.exe $tempWebRoot $repositoryWebRoot /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Could not stage compiled frontend (robocopy $LASTEXITCODE)" }

    $publishArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $publish,
        '-Repository', $Repository, '-Version', $Version, '-OutDir', $OutDir,
        '-SkipFrontendBuild'
    )
    & $powerShell @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish-package.ps1 failed with exit code $LASTEXITCODE" }
} finally {
    if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }
}

$commit = (& git -C $Repository rev-parse --short=8 HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $commit = '' }
$workingTreeChanges = @(& git -C $Repository status --porcelain --untracked-files=all -- . ':(exclude)artifacts/**' 2>$null)
$workingTreeDirty = ($LASTEXITCODE -eq 0) -and ($workingTreeChanges.Count -gt 0)
$dirtySuffix = if ($workingTreeDirty) { '.dirty' } else { '' }
$label = if ($commit) { "$Version+$($commit.Trim())$dirtySuffix" } else { "$Version$dirtySuffix" }
$safeLabel = $label -replace '[^0-9A-Za-z.+_-]', '-'
$payload = Join-Path $OutDir "brmes-$safeLabel"
$installer = Join-Path $Repository 'deploy\BRMES.iss'

$isccArgs = @(
    "/DAppVersion=$Version",
    "/DAppLabel=$safeLabel",
    "/DPayloadDir=$payload",
    "/DRepoRoot=$Repository",
    "/DOutputDir=$OutDir",
    $installer
)
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Join-Path $OutDir "BRMES-Setup-$safeLabel.exe"
$hash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash
"$hash  $(Split-Path -Leaf $setup)" | Set-Content -Path "$setup.sha256" -Encoding ASCII
Write-Host "Installer: $setup"
Write-Host "SHA256:    $hash"
