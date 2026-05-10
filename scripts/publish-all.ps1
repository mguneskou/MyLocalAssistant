# Publishes Server + ServerHost + Admin + Client into a single folder so they
# can run side-by-side the way testers will see them after install.
#
# Usage:  pwsh .\scripts\publish-all.ps1 [-Configuration Release] [-Runtime win-x64] [-OutDir .\dist\MyLocalAssistant]

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime       = "win-x64",
    [string]$OutDir        = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = Resolve-Path (Join-Path $scriptDir "..")
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $root "dist\MyLocalAssistant" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$out = (Resolve-Path $OutDir).Path

Write-Host "Publishing to $out" -ForegroundColor Cyan
Remove-Item -Recurse -Force "$out\*" -ErrorAction SilentlyContinue

$projects = @(
    "src\MyLocalAssistant.Server\MyLocalAssistant.Server.csproj",
    "src\MyLocalAssistant.ServerHost\MyLocalAssistant.ServerHost.csproj",
    "src\MyLocalAssistant.Admin\MyLocalAssistant.Admin.csproj"
)

foreach ($proj in $projects) {
    $name = [IO.Path]::GetFileNameWithoutExtension($proj)
    Write-Host ">> $name" -ForegroundColor Yellow
    dotnet publish (Join-Path $root $proj) `
        -c $Configuration -r $Runtime --self-contained false `
        -p:PublishSingleFile=false `
        -o $out --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $name" }
}

Write-Host ""
Write-Host "Done. Run:  $out\MyLocalAssistant.ServerHost.exe" -ForegroundColor Green
