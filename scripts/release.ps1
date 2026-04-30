# Builds a Velopack release for MyLocalAssistant and (optionally) uploads it to GitHub.
#
# Workflow:
#   1) Publish Server / ServerHost / Admin / Client into a single staging folder.
#   2) `vpk pack` that folder into Setup.exe + delta nupkg + RELEASES manifest.
#   3) (optional) `vpk upload github` to attach them to a GitHub release matching the version.
#
# Pre-reqs:
#   - .NET 8 SDK
#   - vpk global tool (auto-installed if missing)
#
# Examples:
#   .\scripts\release.ps1 -Version 2.1.0
#   .\scripts\release.ps1 -Version 2.1.0 -Upload -GitHubToken $env:GH_PAT
#
# Notes on signing: pass -SignParams "/a /n MyCert /tr http://ts.example/" to have vpk
# Authenticode-sign Setup.exe + Update.exe via signtool. Skip on first tester drops.

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Channel       = "win",
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$PackId        = "MyLocalAssistant",
    [string]$PackTitle     = "MyLocalAssistant",
    [string]$PackAuthors   = "Mehmet Gunes",
    [string]$ReleaseDir    = "",
    [string]$StageDir      = "",
    [string]$RepoUrl       = "https://github.com/mguneskou/MyLocalAssistant",
    [switch]$Upload,
    [string]$GitHubToken   = "",
    [string]$SignParams    = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = (Resolve-Path (Join-Path $scriptDir "..")).Path
if ([string]::IsNullOrWhiteSpace($StageDir))   { $StageDir   = Join-Path $root "dist\stage" }
if ([string]::IsNullOrWhiteSpace($ReleaseDir)) { $ReleaseDir = Join-Path $root "dist\releases" }

# 1) Ensure vpk is installed.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "vpk not found, installing as global .NET tool..." -ForegroundColor Yellow
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw "Failed to install vpk." }
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

# 2) Publish all four exes into the staging folder.
Write-Host "Publishing $Version to $StageDir" -ForegroundColor Cyan
& (Join-Path $scriptDir "publish-all.ps1") -Configuration $Configuration -Runtime $Runtime -OutDir $StageDir
if ($LASTEXITCODE -ne 0) { throw "publish-all failed." }

# 3) Pack with vpk.
if (-not (Test-Path $ReleaseDir)) { New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null }
$packArgs = @(
    "pack",
    "--packId",      $PackId,
    "--packVersion", $Version,
    "--packDir",     $StageDir,
    "--mainExe",     "MyLocalAssistant.ServerHost.exe",
    "--packTitle",   $PackTitle,
    "--packAuthors", $PackAuthors,
    "--outputDir",   $ReleaseDir,
    "--channel",     $Channel
)
if ($SignParams) { $packArgs += @("--signParams", $SignParams) }

Write-Host "vpk $($packArgs -join ' ')" -ForegroundColor Cyan
& vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

# 4) (optional) Upload to a GitHub release.
if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
        throw "Upload requested but -GitHubToken not provided. Pass -GitHubToken `$env:GH_PAT."
    }
    Write-Host "Uploading to $RepoUrl ..." -ForegroundColor Cyan
    & vpk upload github `
        --outputDir $ReleaseDir `
        --repoUrl   $RepoUrl `
        --token     $GitHubToken `
        --tag       "v$Version" `
        --releaseName "MyLocalAssistant $Version" `
        --publish
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed." }
}

Write-Host ""
Write-Host "Release artifacts in $ReleaseDir" -ForegroundColor Green
Get-ChildItem $ReleaseDir | Format-Table Name, Length -AutoSize
