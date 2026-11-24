param(
    [ValidateSet('All', 'Integration', 'UI')][string]$Suite = 'All',
    [string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runId = [guid]::NewGuid().ToString('N')
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $root "work\test-results\$runId" }
$artifacts = New-Item -ItemType Directory -Path $ArtifactDirectory
$scratch = Join-Path ([IO.Path]::GetTempPath()) "AgentFlow-tests-$runId"
$results = [Collections.Generic.List[object]]::new()
$started = [DateTime]::UtcNow

function Invoke-Stage([string]$Name, [scriptblock]$Command) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $artifacts.FullName "$Name.log"
    $checks = [Collections.Generic.List[string]]::new()
    $status = 'PASS'
    $detail = $null
    try {
        & $Command 2>&1 | ForEach-Object {
            $line = [string]$_
            Add-Content -LiteralPath $log -Value $line -Encoding UTF8
            Write-Host $line
            if ($line.StartsWith('PASS ')) { $checks.Add($line.Substring(5)) }
        }
        if ($Name -in 'integration', 'ui' -and $checks.Count -eq 0) { throw 'The suite reported no successful checks.' }
    }
    catch {
        $status = 'FAIL'
        $detail = ($_ | Out-String).Trim()
        Add-Content -LiteralPath $log -Value $detail -Encoding UTF8
        Write-Host $detail
    }
    $results.Add([ordered]@{
        name = $Name; status = $status; durationMs = $clock.ElapsedMilliseconds
        checks = $checks.ToArray(); detail = $detail; log = "$Name.log"
    })
    return $status -eq 'PASS'
}

function Invoke-DotNet([string[]]$Arguments) {
    $global:LASTEXITCODE = $null
    $ErrorActionPreference = 'Continue'
    & dotnet @Arguments 2>&1 | ForEach-Object { [string]$_ }
    if ($global:LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed (exit $global:LASTEXITCODE)." }
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $app = Join-Path $scratch 'app'
    $fixture = Join-Path $scratch 'fixture'
    $built = Invoke-Stage 'publish' {
        Invoke-DotNet @('publish', (Join-Path $root 'AgentFlow.csproj'), '-c', 'Release', '-o', $app)
        Invoke-DotNet @('publish', (Join-Path $PSScriptRoot 'ClaudeFixture\ClaudeFixture.csproj'), '-c', 'Release', '-o', $fixture)
    }
    $executable = Join-Path $app 'AgentFlow.exe'
    $fixtureExe = Join-Path $fixture 'claude.exe'
    if ($built -and $Suite -in 'All', 'Integration') {
        $null = Invoke-Stage 'integration' {
            Invoke-DotNet @('run', '--project', (Join-Path $PSScriptRoot 'AgentFlow.Tests.csproj'), '-c', 'Release', '--',
                $executable, $fixtureExe, $scratch)
        }
    }
    if ($built -and $Suite -in 'All', 'UI') {
        $null = Invoke-Stage 'ui' {
            & (Join-Path $PSScriptRoot 'smoke-ui.ps1') -Executable $executable -Fixture $fixtureExe -Sandbox $scratch -ArtifactDirectory $artifacts.FullName
        }
    }
}
catch {
    $results.Add([ordered]@{ name = 'setup'; status = 'FAIL'; detail = "$_" })
}
finally {
    if ([IO.Directory]::Exists($scratch)) {
        try {
            $resolved = (Resolve-Path -LiteralPath $scratch).ProviderPath
            $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "AgentFlow-tests-$runId"))
            if ($resolved -ne $expected -or ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unexpected test scratch path: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        catch { $results.Add([ordered]@{ name = 'cleanup'; status = 'FAIL'; detail = "$_" }) }
    }
    $failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
    $report = [ordered]@{
        schemaVersion = 1; runId = $runId; startedUtc = $started.ToString('o')
        suite = $Suite; target = 'local'; failedStages = $failed; stages = $results.ToArray()
        screenshots = @(Get-ChildItem -LiteralPath $artifacts.FullName -Filter '*.png' -File | ForEach-Object Name)
    }
    $reportPath = Join-Path $artifacts.FullName 'test-results.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "Evaluation report: $reportPath"
}
if ($failed) { exit 1 }
exit 0
