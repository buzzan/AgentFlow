param([string]$Executable, [string]$Fixture, [string]$Sandbox)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$project = New-Item -ItemType Directory -Path (Join-Path $Sandbox 'ui-project')
$agentDirectory = New-Item -ItemType Directory -Path (Join-Path $project.FullName '.claude\agents') -Force
Set-Content -LiteralPath (Join-Path $agentDirectory.FullName 'fixture.md') -Value @('---', 'name: fixture-agent', '---')
$jobRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClaudeAgentScheduler'

function Find-TestJob {
    foreach ($folder in Get-ChildItem -LiteralPath $jobRoot -Directory -ErrorAction SilentlyContinue) {
        $jobFile = Join-Path $folder.FullName 'job.xml'
        if (Test-Path -LiteralPath $jobFile) {
            try { $job = ([xml](Get-Content -LiteralPath $jobFile -Raw)).Job } catch { continue }
            if ($job.Project -eq $project.FullName -and $job.Claude -eq $Fixture -and $job.Id -eq $folder.Name) { return $folder }
        }
    }
}

$process = Start-Process -FilePath $Executable -PassThru
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "AgentFlow exited with code $($process.ExitCode)." }
    } until ($process.MainWindowHandle -ne 0 -or $watch.Elapsed.TotalSeconds -ge 15)
    if ($process.MainWindowHandle -eq 0) { throw 'AgentFlow window did not open.' }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    function Find-Button([string]$Name) {
        $condition = New-Object Windows.Automation.PropertyCondition `
            ([Windows.Automation.AutomationElement]::NameProperty, $Name)
        $matches = $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($match in $matches) {
            if ($match.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { return $match }
        }
        throw "Button not found: $Name"
    }

    function Invoke-Button([string]$Name) {
        (Find-Button $Name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    function Set-Field([string]$Name, [string]$Value) {
        $condition = New-Object Windows.Automation.PropertyCondition `
            ([Windows.Automation.AutomationElement]::NameProperty, $Name)
        foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            $pattern = $null
            if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
                $pattern.SetValue($Value)
                return
            }
        }
        throw "Editable field not found: $Name"
    }

    foreach ($name in 'Browse Folder', 'Browse File', 'Reload Agents', 'Schedule', 'Refresh') {
        if (-not (Find-Button $name).Current.IsEnabled) { throw "Expected enabled button: $name" }
    }
    foreach ($name in 'Open Log', 'Cancel Schedule') {
        if ((Find-Button $name).Current.IsEnabled) { throw "Expected disabled button before selection: $name" }
    }
    Invoke-Button 'Refresh'
    Set-Field 'Project' $project.FullName
    Set-Field 'Claude CLI' $Fixture
    Invoke-Button 'Reload Agents'
    Set-Field 'Agent' 'fixture-agent'
    Set-Field 'Task Instructions' 'scheduled through the UI'
    Invoke-Button 'Schedule'
    $watch.Restart()
    do {
        Start-Sleep -Milliseconds 100
        $jobFolder = Find-TestJob
    } until (($jobFolder -and (Find-Button 'Cancel Schedule').Current.IsEnabled) -or $watch.Elapsed.TotalSeconds -ge 10)
    if (-not $jobFolder -or -not (Find-Button 'Cancel Schedule').Current.IsEnabled) { throw 'UI did not register and select the schedule.' }
    if (-not $process.WaitForInputIdle(5000)) { throw 'UI did not finish updating after registration.' }
    Invoke-Button 'Cancel Schedule'
    $watch.Restart()
    $canceled = Join-Path $jobFolder.FullName 'canceled'
    while (-not (Test-Path -LiteralPath $canceled) -and $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $canceled)) {
        $controls = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $details = foreach ($control in $controls) { if ($control.Current.Name) { $control.Current.Name } }
        throw ('UI did not cancel the schedule. Visible controls: ' + ($details -join ', '))
    }
    $watch.Restart()
    while ((Find-Button 'Cancel Schedule').Current.IsEnabled -and $watch.Elapsed.TotalSeconds -lt 10) {
        Start-Sleep -Milliseconds 100
    }
    if ((Find-Button 'Cancel Schedule').Current.IsEnabled) { throw 'Canceled schedule remained selected.' }
    $rowName = New-Object Windows.Automation.PropertyCondition `
        ([Windows.Automation.AutomationElement]::NameProperty, $project.FullName)
    if ($window.FindFirst([Windows.Automation.TreeScope]::Descendants, $rowName)) {
        throw 'Canceled schedule still appears in the list.'
    }
    Write-Output 'PASS UI startup, Refresh, registration, selection and removal after cancellation'
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    $process.Dispose()
    $jobFolder = Find-TestJob
    if ($jobFolder -and $jobFolder.Name -match '^[a-f0-9]{32}$') {
        $task = Get-ScheduledTask -TaskPath '\' -TaskName "ClaudeAgentScheduler-$($jobFolder.Name)" -ErrorAction SilentlyContinue
        if ($task) { $task | Unregister-ScheduledTask -Confirm:$false }
        [IO.Directory]::Delete($jobFolder.FullName, $true)
    }
}
