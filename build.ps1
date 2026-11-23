$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 /optimize+ "/out:$output\AgentFlow.exe" /reference:Microsoft.CSharp.dll "$PSScriptRoot\Scheduler.cs" "$PSScriptRoot\SchedulerForm.cs"
if ($LASTEXITCODE -ne 0) { throw "빌드 실패 ($LASTEXITCODE)" }
