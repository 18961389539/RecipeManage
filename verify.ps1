<#
.SYNOPSIS
    One-command regression for the developer box: backend tests, frontend unit tests, frontend build,
    and (with -WithE2e) a live smoke check plus the Playwright suite.

.DESCRIPTION
    Why this exists: the full regression used to be a sequence of commands kept in someone's head
    (four `dotnet test` invocations, `npm run test:unit`, `npm run build`, start the API by hand,
    curl a few endpoints, then `npx playwright test`). Every manual sequence drifts - a new test
    project gets forgotten, the smoke check stops happening. This script is that sequence, in order,
    with a non-zero exit code when any step fails.

    Default mode needs no running processes: it builds everything and runs the fast suites.
    -WithE2e reuses a healthy API on port 5010 if one is already running (a dev instance is left
    alone); otherwise it starts one and stops it again afterwards.

    Close a manually started `dotnet run` API before running this: dotnet test rebuilds the API and
    cannot write the output files while the app is running.

.PARAMETER WithE2e
    Also run the smoke check (SPA served from the API, unknown /api stays 404) and `npx playwright test`.

.PARAMETER E2eSpec
    Run only this Playwright spec file (e.g. "genealogy.spec.ts") instead of the whole suite.
    Useful while iterating; empty means the full suite.

.EXAMPLE
    powershell -File verify.ps1
    powershell -File verify.ps1 -WithE2e
    powershell -File verify.ps1 -WithE2e -E2eSpec genealogy.spec.ts
#>
[CmdletBinding()]
param(
    [switch] $WithE2e,
    [string] $E2eSpec = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step { param([string] $Message) Write-Host "==> $Message" }

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
$frontend = Join-Path $scriptRoot 'frontend'
$healthUrl = 'http://localhost:5010/health'

try {
    # ---- 1. backend: every test project, so a new one cannot be forgotten ----
    $projects = @(
        'tests\RecipesManage.Domain.Tests',
        'tests\RecipesManage.Contracts.Tests',
        'tests\RecipesManage.Api.Tests',
        'tests\RecipesManage.Execution.Tests'
    )
    foreach ($project in $projects) {
        Step "dotnet test $project"
        & dotnet test (Join-Path $scriptRoot $project) --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed for $project (exit $LASTEXITCODE)" }
    }

    # ---- 2. frontend: unit tests, then the real build (vue-tsc + vite -> Api wwwroot) ----
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'npm not on PATH (needed for frontend tests and build)' }
    Push-Location $frontend
    try {
        Step 'npm run test:unit'
        & npm run test:unit --silent
        if ($LASTEXITCODE -ne 0) { throw "vitest failed (exit $LASTEXITCODE)" }

        Step 'npm run build (vue-tsc type check + vite build into src\RecipesManage.Api\wwwroot)'
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "frontend build failed (exit $LASTEXITCODE)" }
    } finally { Pop-Location }

    # ---- 3. optional: live smoke + e2e ----
    if ($WithE2e) {
        $startedApi = $null
        $healthy = ((& curl.exe -s -o NUL -w "%{http_code}" $healthUrl) -eq '200')
        if ($healthy) {
            Write-Host '   API already healthy on 5010 - reusing it (it will not be stopped)'
        } else {
            Step 'start API on http://localhost:5010 (for smoke + e2e)'
            $apiProject = Join-Path $scriptRoot 'src\RecipesManage.Api'
            $apiOut = Join-Path $env:TEMP 'brmes-verify-api.log'
            $apiErr = Join-Path $env:TEMP 'brmes-verify-api.err'
            $startedApi = Start-Process -FilePath dotnet `
                -ArgumentList @('run', '--project', $apiProject, '--launch-profile', 'http', '--no-build') `
                -PassThru -WindowStyle Hidden -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr
            for ($i = 0; $i -lt 80; $i++) {
                Start-Sleep -Milliseconds 500
                if ((& curl.exe -s -o NUL -w "%{http_code}" $healthUrl) -eq '200') { $healthy = $true; break }
            }
            if (-not $healthy) {
                Get-Content $apiOut, $apiErr -ErrorAction SilentlyContinue | Select-Object -Last 20
                throw 'API did not become healthy within 40s (log tail above)'
            }
        }

        try {
            Step 'smoke: SPA is served from the API and unknown /api routes stay 404'
            # -join the lines: on a collection `-notmatch` filters elements instead of returning a bool.
            $index = (& curl.exe -s 'http://localhost:5010/') -join "`n"
            if ($index -notmatch 'id="app"') { throw 'GET / did not return the SPA shell - is src\RecipesManage.Api\wwwroot built?' }
            $code = & curl.exe -s -o NUL -w "%{http_code}" 'http://localhost:5010/api/__no_such_route__'
            if ($code -ne '404') { throw "unknown /api route returned $code (expected 404 JSON, not the SPA shell)" }

            Step "npx playwright test $E2eSpec"
            Push-Location $frontend
            try {
                if ($E2eSpec) { & npx playwright test $E2eSpec } else { & npx playwright test }
                if ($LASTEXITCODE -ne 0) { throw "playwright failed (exit $LASTEXITCODE)" }
            } finally { Pop-Location }
        } finally {
            if ($startedApi) {
                taskkill /F /IM RecipesManage.Api.exe 2>$null | Out-Null
                Stop-Process -Id $startedApi.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host ''
    Write-Host 'VERIFY OK'
} catch {
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 2
}