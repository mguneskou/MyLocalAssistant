<#
.SYNOPSIS
    Stops and removes the MyLocalAssistant Windows service. Optionally deletes the install directory.

.PARAMETER InstallPath
    Install directory to remove. Defaults to C:\Program Files\MyLocalAssistant\Server.

.PARAMETER PurgeData
    Also delete data\, logs\, vectors\, models\, ingestion\, config\. Without this,
    only the service registration is removed; the directory stays untouched.
#>
[CmdletBinding()]
param(
    [string] $InstallPath = "C:\Program Files\MyLocalAssistant\Server",
    [switch] $PurgeData
)

$ErrorActionPreference = "Stop"
$ServiceName = "MyLocalAssistantServer"

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$p = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated PowerShell."
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $svc) {
    if ($svc.Status -ne "Stopped") {
        Write-Host "Stopping service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Write-Host "Removing service '$ServiceName'..."
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
} else {
    Write-Host "Service '$ServiceName' not installed."
}

if ($PurgeData) {
    if (Test-Path $InstallPath) {
        Write-Warning "Deleting $InstallPath (including data, logs, models, vectors, ingestion, config)."
        Remove-Item -Recurse -Force $InstallPath
    }
} else {
    Write-Host "Install path left in place: $InstallPath"
    Write-Host "(use -PurgeData to also delete it)"
}
