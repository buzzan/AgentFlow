param([string]$DotNet = 'dotnet')

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'AgentFlow.csproj'
$output = Join-Path $PSScriptRoot 'dist\daily-row-toggle'
& $DotNet publish $project -c Release -o $output
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
