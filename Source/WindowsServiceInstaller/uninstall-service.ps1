#requires -Version 7.0
<#
.SYNOPSIS
    Stops and removes the Roslynk Windows service. Run from an elevated (Administrator) prompt.
#>
[CmdletBinding()]
param(
    [string] $ServiceName = 'Roslynk'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Host "Service '$ServiceName' is not installed."
    return
}

Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
sc.exe delete $ServiceName | Out-Null
Write-Host "Service '$ServiceName' removed."
