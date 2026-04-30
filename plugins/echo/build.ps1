# Build, sign, and pack the Echo reference plug-in.
#
# Usage:
#   pwsh ./plugins/echo/build.ps1                       # default: dev key in plugins/echo/.keys/
#   pwsh ./plugins/echo/build.ps1 -KeyId acme           # use config/trusted-keys/acme.pub + .keys/acme.key
#   pwsh ./plugins/echo/build.ps1 -InstallTo ./out/srv  # also install into <out/srv>/plugins/echo/
#
# Re-running with the same -KeyId reuses the existing keypair (idempotent).

param(
    [string]$KeyId = "dev",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InstallTo = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$plugin = $PSScriptRoot
$skillTools = Join-Path $root "tools\MyLocalAssistant.SkillTools\MyLocalAssistant.SkillTools.csproj"
$keysDir = Join-Path $plugin ".keys"
$trustedDir = Join-Path $root "src\MyLocalAssistant.Server\config\trusted-keys"
$pubKey = Join-Path $trustedDir "$KeyId.pub"
$privKey = Join-Path $keysDir "$KeyId.key"
$publishDir = Join-Path $plugin "publish"
$pkgPath = Join-Path $plugin "publish\$KeyId-echo.mlaplugin"

Write-Host "==> Publishing echo ($Configuration / $Runtime)"
dotnet publish (Join-Path $plugin "echo.csproj") -c $Configuration -r $Runtime --self-contained false -o $publishDir | Out-Null

Write-Host "==> Ensuring keypair '$KeyId'"
New-Item -ItemType Directory -Force -Path $keysDir, $trustedDir | Out-Null
if (-not (Test-Path $pubKey) -or -not (Test-Path $privKey)) {
    dotnet run --project $skillTools -c $Configuration -- keygen --pub $pubKey --priv $privKey | Out-Null
} else {
    Write-Host "    (reusing existing $pubKey)"
}

Write-Host "==> Writing manifest.json"
$template = Get-Content (Join-Path $plugin "manifest.template.json") -Raw
$manifestPath = Join-Path $publishDir "manifest.json"
($template -replace "REPLACE-WITH-KEY-ID", $KeyId) | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "==> Hashing payload"
dotnet run --project $skillTools -c $Configuration -- hash $publishDir | Out-Null

Write-Host "==> Signing manifest"
dotnet run --project $skillTools -c $Configuration -- sign $manifestPath --key $privKey | Out-Null

Write-Host "==> Packing"
dotnet run --project $skillTools -c $Configuration -- pack $publishDir --out $pkgPath | Out-Null
Write-Host "Package: $pkgPath"

if ($InstallTo) {
    Write-Host "==> Installing to $InstallTo"
    dotnet run --project $skillTools -c $Configuration -- install $pkgPath --to $InstallTo | Out-Null
    # Trust store is per-install: copy the public key alongside.
    $destTrusted = Join-Path $InstallTo "config\trusted-keys"
    New-Item -ItemType Directory -Force -Path $destTrusted | Out-Null
    Copy-Item $pubKey $destTrusted -Force
    Write-Host "Trusted key '$KeyId' copied to $destTrusted"
}
