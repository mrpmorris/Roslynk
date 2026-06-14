#requires -Version 7.0
<#
.SYNOPSIS
    Registers and starts Roslynk as a Windows service directly from a publish folder — the no-MSI path,
    equivalent to what the WiX package does. Run from an elevated (Administrator) prompt.
.PARAMETER PublishDir
    The self-contained publish output (default: installer/publish, produced by publish.ps1).
#>
[CmdletBinding()]
param(
    [string] $PublishDir = (Join-Path $PSScriptRoot 'publish'),
    [string] $ServiceName = 'Roslynk'
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $PublishDir 'Morris.Roslynk.Mcp.exe'
if (-not (Test-Path $exe)) {
    throw "Service host not found at '$exe'. Run publish.ps1 first."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service '$ServiceName' already exists; stopping and removing it first."
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $ServiceName `
    -BinaryPathName "`"$exe`"" `
    -DisplayName 'Roslynk — C# semantic intelligence' `
    -Description 'Roslyn-based C# semantic intelligence MCP server. Listens on loopback only.' `
    -StartupType Automatic | Out-Null

Start-Service -Name $ServiceName
Write-Host "Service '$ServiceName' installed and started."
