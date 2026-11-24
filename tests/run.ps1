$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('AgentFlow-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null

try {
    $app = Join-Path $sandbox 'app'
    $fixture = Join-Path $sandbox 'fixture'
    dotnet publish (Join-Path $root 'AgentFlow.csproj') -c Release -o $app
    if ($LASTEXITCODE -ne 0) { throw 'AgentFlow publish failed.' }
    dotnet publish (Join-Path $PSScriptRoot 'ClaudeFixture\ClaudeFixture.csproj') -c Release -o $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Claude fixture publish failed.' }
    dotnet run --project (Join-Path $PSScriptRoot 'AgentFlow.Tests.csproj') -c Release -- `
        (Join-Path $app 'AgentFlow.exe') (Join-Path $fixture 'claude.exe') $sandbox
    if ($LASTEXITCODE -ne 0) { throw 'AgentFlow integration tests failed.' }
    & (Join-Path $PSScriptRoot 'smoke-ui.ps1') -Executable (Join-Path $app 'AgentFlow.exe') `
        -Fixture (Join-Path $fixture 'claude.exe') -Sandbox $sandbox
}
finally {
    [IO.Directory]::Delete($sandbox, $true)
}
