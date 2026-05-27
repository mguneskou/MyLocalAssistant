<#
.SYNOPSIS
    Publishes MyLocalAssistant.Server, optionally generates a self-signed TLS cert,
    writes the listen URL into config\server.json, and registers a Windows service.

.DESCRIPTION
    Run from an elevated PowerShell. Idempotent — safe to re-run for upgrades.
    The server is published self-contained for win-x64 so the target box does not
    need the .NET runtime installed.

.PARAMETER InstallPath
    Target install directory. Defaults to C:\Program Files\MyLocalAssistant\Server.

.PARAMETER ListenHost
    Host or IP to bind. Defaults to 0.0.0.0 (all interfaces).

.PARAMETER Port
    TCP port. Defaults to 8443 when -EnableHttps, else 8080.

.PARAMETER EnableHttps
    Bind https://. If -CertificatePath is omitted, generates a self-signed cert
    valid for 825 days (CN=<machine FQDN>) and exports it as a PFX into <InstallPath>\config.

.PARAMETER CertificatePath
    Path to an existing PFX file. Copied into <InstallPath>\config.

.PARAMETER CertificatePassword
    Password for -CertificatePath. Ignored when generating a self-signed cert
    (the generated PFX is written with an empty password and protected by NTFS ACLs
    on the config directory; lock down further per your policy).

.PARAMETER ServiceAccount
    Account the service runs under. Defaults to "NT AUTHORITY\NetworkService".

.EXAMPLE
    .\install-service.ps1 -EnableHttps

.EXAMPLE
    .\install-service.ps1 -InstallPath D:\MLA\Server -Port 9000
#>
[CmdletBinding()]
param(
    [string] $InstallPath = "C:\Program Files\MyLocalAssistant\Server",
    [string] $ListenHost = "0.0.0.0",
    [int] $Port = 0,
    [switch] $EnableHttps,
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $ServiceAccount = "NT AUTHORITY\NetworkService",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ServiceName = "MyLocalAssistantServer"
$DisplayName = "MyLocalAssistant Server"
$Description = "Local LLM, RAG, and chat API for MyLocalAssistant clients."

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run from an elevated PowerShell."
    }
}

function Resolve-RepoRoot {
    # scripts\install-service.ps1 -> repo root
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Stop-AndDeleteServiceIfExists {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $svc) {
        Write-Host "Stopping existing service '$ServiceName'..."
        if ($svc.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
        }
        Write-Host "Removing existing service registration..."
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }
}

function Publish-Server($repoRoot, $publishDir) {
    $proj = Join-Path $repoRoot "src\MyLocalAssistant.Server\MyLocalAssistant.Server.csproj"
    if (-not (Test-Path $proj)) { throw "Server project not found at $proj" }
    Write-Host "Publishing server (Configuration=$Configuration, win-x64, self-contained)..."
    & dotnet publish $proj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=embedded -o $publishDir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}

function New-SelfSignedPfx($pfxPath) {
    $hostName = ([System.Net.Dns]::GetHostEntry([Environment]::MachineName)).HostName
    if ([string]::IsNullOrWhiteSpace($hostName)) { $hostName = $env:COMPUTERNAME }
    $dnsNames = @($hostName, $env:COMPUTERNAME, "localhost") | Select-Object -Unique
    Write-Host "Generating self-signed certificate for: $($dnsNames -join ', ')"
    $cert = New-SelfSignedCertificate `
        -DnsName $dnsNames `
        -CertStoreLocation "cert:\LocalMachine\My" `
        -KeyExportPolicy Exportable `
        -KeyAlgorithm RSA -KeyLength 2048 `
        -NotAfter (Get-Date).AddDays(825) `
        -FriendlyName "MyLocalAssistant TLS"
    try {
        $pwd = ConvertTo-SecureString -String " " -AsPlainText -Force
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
    }
    finally {
        Remove-Item "cert:\LocalMachine\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    }
    Write-Host "Self-signed PFX written to $pfxPath"
    return ""
}

function Update-ServerJson($installPath, $listenUrl, $certPath, $certPwd) {
    $configDir = Join-Path $installPath "config"
    $configFile = Join-Path $configDir "server.json"
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null

    if (Test-Path $configFile) {
        $json = Get-Content $configFile -Raw | ConvertFrom-Json
    } else {
        $json = [ordered]@{}
    }

    # Convert PSCustomObject to ordered hashtable for predictable serialization
    $h = [ordered]@{}
    if ($json -is [System.Management.Automation.PSCustomObject]) {
        $json.PSObject.Properties | ForEach-Object { $h[$_.Name] = $_.Value }
    } elseif ($json -is [System.Collections.IDictionary]) {
        foreach ($k in $json.Keys) { $h[$k] = $json[$k] }
    }
    $h["listenUrl"] = $listenUrl
    if ($certPath) {
        $h["certificatePath"] = $certPath
        $h["certificatePassword"] = $certPwd
    } else {
        $h.Remove("certificatePath") | Out-Null
        $h.Remove("certificatePassword") | Out-Null
    }
    ($h | ConvertTo-Json -Depth 10) | Set-Content -Path $configFile -Encoding UTF8
    Write-Host "Wrote $configFile"
}

function Grant-ConfigAcl($installPath, $account) {
    $configDir = Join-Path $installPath "config"
    if (-not (Test-Path $configDir)) { return }
    $acl = Get-Acl $configDir
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $account, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl -Path $configDir -AclObject $acl
}

function Grant-WriteAcl($installPath, $account, $sub) {
    $dir = Join-Path $installPath $sub
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $acl = Get-Acl $dir
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $account, "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl -Path $dir -AclObject $acl
}

function Register-Service($exePath, $account) {
    Write-Host "Registering Windows service '$ServiceName'..."
    $binPath = "`"$exePath`""
    & sc.exe create $ServiceName binPath= $binPath start= auto obj= $account DisplayName= "$DisplayName" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed (exit $LASTEXITCODE)." }
    & sc.exe description $ServiceName "$Description" | Out-Null
    # Restart on failure: 1st 5s, 2nd 30s, subsequent 60s; reset counter every 1h.
    & sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    & sc.exe start $ServiceName | Out-Host
}

# --- main ---
Assert-Admin

if ($Port -eq 0) { $Port = if ($EnableHttps) { 8443 } else { 8080 } }
$scheme = if ($EnableHttps) { "https" } else { "http" }
$listenUrl = "${scheme}://${ListenHost}:${Port}"
$repoRoot = Resolve-RepoRoot

$publishDir = Join-Path $InstallPath ""  # publish straight into InstallPath
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null

Stop-AndDeleteServiceIfExists
Publish-Server $repoRoot $publishDir

# TLS handling
$certCopiedPath = $null
$certPwdPlain = $null
if ($EnableHttps) {
    $configDir = Join-Path $InstallPath "config"
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    if ($CertificatePath) {
        if (-not (Test-Path $CertificatePath)) { throw "CertificatePath not found: $CertificatePath" }
        $certCopiedPath = Join-Path $configDir "tls.pfx"
        Copy-Item -Path $CertificatePath -Destination $certCopiedPath -Force
        $certPwdPlain = $CertificatePassword
    }
    else {
        $certCopiedPath = Join-Path $configDir "tls.pfx"
        $certPwdPlain = New-SelfSignedPfx -pfxPath $certCopiedPath
    }
}

Update-ServerJson -installPath $InstallPath -listenUrl $listenUrl -certPath $certCopiedPath -certPwd $certPwdPlain
Grant-ConfigAcl -installPath $InstallPath -account $ServiceAccount
Grant-WriteAcl -installPath $InstallPath -account $ServiceAccount -sub "data"
Grant-WriteAcl -installPath $InstallPath -account $ServiceAccount -sub "logs"
Grant-WriteAcl -installPath $InstallPath -account $ServiceAccount -sub "vectors"
Grant-WriteAcl -installPath $InstallPath -account $ServiceAccount -sub "models"
Grant-WriteAcl -installPath $InstallPath -account $ServiceAccount -sub "ingestion"
Grant-WriteAcl -installPath $InstallPath -account $ServiceAccount -sub "config"

$exePath = Join-Path $InstallPath "MyLocalAssistant.Server.exe"
if (-not (Test-Path $exePath)) { throw "Published exe not found at $exePath" }

Register-Service -exePath $exePath -account $ServiceAccount

if ($EnableHttps -and -not $CertificatePath) {
    Write-Warning "Self-signed certificate generated. Clients will see a trust warning until you import the PFX into their Trusted Root store."
}
Write-Host ""
Write-Host "Done. Service '$ServiceName' is running and listening on $listenUrl"
Write-Host "Install path: $InstallPath"
