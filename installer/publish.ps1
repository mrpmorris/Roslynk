#requires -Version 7.0
<#
.SYNOPSIS
    Publishes the Roslynk service as a self-contained win-x64 build into installer/publish,
    ready for the WiX MSI (Morris.Roslynk.Installer.wixproj) to harvest.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Source\Morris.Roslynk.Mcp\Morris.Roslynk.Mcp.csproj'
$output = Join-Path $PSScriptRoot 'publish'

if (Test-Path $output) { Remove-Item -Recurse -Force $output }

dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=false `
    -o $output

Write-Host "Published to $output"
