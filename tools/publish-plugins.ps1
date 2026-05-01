<#
.SYNOPSIS
    Publishes all bundled plugins, signs them with PluginSigner, and verifies the signatures.

.DESCRIPTION
    1. Publishes each plugin exe to <BundledDir>/plugins/<manifest-id>/
    2. Generates a signing key pair (bundled-plugins.key / bundled-plugins.pub) under
       tools/keys/ if not already present.
    3. Signs each plugin folder with the key.
    4. Copies bundled-plugins.pub into <BundledDir>/config/trusted-keys/ so it is
       embedded in the server publish output and seeded into the state directory on first run.
    5. Verifies each signature, then optionally publishes the server itself.

.PARAMETER Configuration
    Build configuration: Debug or Release (default: Release).

.PARAMETER BundledDir
    Root of the bundled asset tree to write into. Defaults to
    src\MyLocalAssistant.Server\bundled (for local dev). CI passes dist\stage\bundled.

.PARAMETER PublishServer
    When set, also publishes the server project after bundling the plugins.

.EXAMPLE
    .\tools\publish-plugins.ps1
    .\tools\publish-plugins.ps1 -Configuration Debug -PublishServer
    .\tools\publish-plugins.ps1 -BundledDir "$PWD\dist\stage\bundled" -Runtime win-x64  # CI usage
#>
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$BundledDir    = '',
    [switch]$PublishServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Paths ───────────────────────────────────────────────────────────────────

$Root       = $PSScriptRoot | Split-Path   # repo root (tools\.. → repo root)
if ([string]::IsNullOrWhiteSpace($BundledDir)) {
    $BundledDir = Join-Path $Root 'src\MyLocalAssistant.Server\bundled'
}
$BundledDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BundledDir)
$PluginsOut   = Join-Path $BundledDir 'plugins'
$KeysOut      = Join-Path $BundledDir 'config\trusted-keys'
$KeysDir      = Join-Path $Root 'tools\keys'
$KeyId        = 'bundled-plugins'
$KeyFile      = Join-Path $KeysDir "$KeyId.key"
$PubFile      = Join-Path $KeysDir "$KeyId.pub"
$SignerProj   = Join-Path $Root 'tools\PluginSigner\MyLocalAssistant.Tools.PluginSigner.csproj'
$ServerProj   = Join-Path $Root 'src\MyLocalAssistant.Server\MyLocalAssistant.Server.csproj'

# Plugin definitions: (csproj path, manifest id used as output folder name)
$Plugins = @(
    @{ Proj = 'src\MyLocalAssistant.Plugins\WebSearch\MyLocalAssistant.Plugins.WebSearch.csproj';         Id = 'web.search'   }
    @{ Proj = 'src\MyLocalAssistant.Plugins\CodeInterpreter\MyLocalAssistant.Plugins.CodeInterpreter.csproj'; Id = 'code.csharp' }
    @{ Proj = 'src\MyLocalAssistant.Plugins\ImageGen\MyLocalAssistant.Plugins.ImageGen.csproj';           Id = 'image.gen'    }
    @{ Proj = 'src\MyLocalAssistant.Plugins\MemoryTool\MyLocalAssistant.Plugins.MemoryTool.csproj';       Id = 'memory.tool'  }
    @{ Proj = 'src\MyLocalAssistant.Plugins\ReportGen\MyLocalAssistant.Plugins.ReportGen.csproj';         Id = 'report.gen'   }
    @{ Proj = 'src\MyLocalAssistant.Plugins\EmailTool\MyLocalAssistant.Plugins.EmailTool.csproj';         Id = 'email.tool'   }
    @{ Proj = 'src\MyLocalAssistant.Plugins\Scheduler\MyLocalAssistant.Plugins.Scheduler.csproj';         Id = 'scheduler'    }
)

# ── Helpers ──────────────────────────────────────────────────────────────────

function Invoke-Dotnet {
    Write-Host "  dotnet $($args -join ' ')" -ForegroundColor DarkGray
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed (exit $LASTEXITCODE)" }
}

function Invoke-Signer {
    Write-Host "  signer $($args -join ' ')" -ForegroundColor DarkGray
    & dotnet run --project $SignerProj --no-build -- @args
    if ($LASTEXITCODE -ne 0) { throw "PluginSigner $($args[0]) failed (exit $LASTEXITCODE)" }
}

# ── Step 0: Build PluginSigner once ──────────────────────────────────────────

Write-Host "`n[0/5] Building PluginSigner..." -ForegroundColor Cyan
Invoke-Dotnet build $SignerProj -c $Configuration --nologo -v q

# ── Step 1: Generate signing key if missing ───────────────────────────────────

Write-Host "`n[1/5] Signing key..." -ForegroundColor Cyan
if (-not (Test-Path $KeyFile)) {
    $null = New-Item -ItemType Directory -Force -Path $KeysDir
    Write-Host "  Generating new key pair: $KeyId" -ForegroundColor Yellow
    Push-Location $KeysDir
    try   { Invoke-Signer generate-key $KeyId }
    finally { Pop-Location }
} else {
    Write-Host "  Key already exists: $KeyFile"
}

# ── Step 2: Publish plugins ───────────────────────────────────────────────────

Write-Host "`n[2/5] Publishing plugins ($Configuration)..." -ForegroundColor Cyan
$null = New-Item -ItemType Directory -Force -Path $PluginsOut

foreach ($p in $Plugins) {
    $dest = Join-Path $PluginsOut $p.Id
    Write-Host "  → $($p.Id)" -ForegroundColor White
    Invoke-Dotnet publish (Join-Path $Root $p.Proj) `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -o $dest `
        --nologo -v q
}

# ── Step 3: Sign each plugin folder ───────────────────────────────────────────

Write-Host "`n[3/5] Signing plugin folders..." -ForegroundColor Cyan
foreach ($p in $Plugins) {
    $folder = Join-Path $PluginsOut $p.Id
    Write-Host "  → signing $($p.Id)" -ForegroundColor White
    Invoke-Signer sign $folder $KeyFile
}

# ── Step 4: Copy public key into bundled trusted-keys ─────────────────────────

Write-Host "`n[4/5] Copying public key to bundled/config/trusted-keys..." -ForegroundColor Cyan
$null = New-Item -ItemType Directory -Force -Path $KeysOut
$destPub = Join-Path $KeysOut "$KeyId.pub"
Copy-Item -Path $PubFile -Destination $destPub -Force
Write-Host "  → $destPub"

# ── Step 5: Verify signatures ─────────────────────────────────────────────────

Write-Host "`n[5/5] Verifying signatures..." -ForegroundColor Cyan
foreach ($p in $Plugins) {
    $folder = Join-Path $PluginsOut $p.Id
    Write-Host "  → verifying $($p.Id)" -ForegroundColor White
    Invoke-Signer verify $folder $PubFile
}

# ── Optional: Publish server ──────────────────────────────────────────────────

if ($PublishServer) {
    $serverOut = Join-Path $Root "publish\server"
    Write-Host "`n[+] Publishing server to $serverOut ..." -ForegroundColor Cyan
    Invoke-Dotnet publish $ServerProj `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -o $serverOut `
        --nologo -v q
    Write-Host "  Server published."
}

Write-Host "`nDone." -ForegroundColor Green
