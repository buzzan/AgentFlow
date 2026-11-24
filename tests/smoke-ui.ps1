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
            if ($job.Project -eq $project.FullName -and $job.Claude -eq $Fixture -and $job.Id -eq $folder.Name) { $folder }
        }
    }
}

$process = $null
$damagedJob = $null
try {
    $damagedJob = New-Item -ItemType Directory -Path (Join-Path $jobRoot ([guid]::NewGuid().ToString('N'))) -Force
    Set-Content -LiteralPath (Join-Path $damagedJob.FullName 'job.xml') -Value '<Job>'
    $process = Start-Process -FilePath $Executable -PassThru
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
        if (-not $process.WaitForInputIdle(5000)) { throw "UI did not finish processing '$Name'." }
    }

    function Wait-ForState([string]$Description, [scriptblock]$Condition) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            if (& $Condition) { return }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 10)
        throw "Timed out waiting for $Description."
    }

    function Test-SelectionActions([bool]$Enabled) {
        ((Find-Button 'Open Log').Current.IsEnabled -eq $Enabled) -and
            ((Find-Button 'Cancel Schedule').Current.IsEnabled -eq $Enabled)
    }

    function Find-NamedControl([string]$Name) {
        $condition = New-Object Windows.Automation.PropertyCondition `
            ([Windows.Automation.AutomationElement]::NameProperty, $Name)
        $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
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
    Invoke-Button 'Reload Agents'
    Wait-ForState 'disabled selection actions after Refresh and Reload Agents' { Test-SelectionActions $false }
    Write-Output 'PASS UI without selection: Refresh then Reload Agents'
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
    Invoke-Button 'Refresh'
    Invoke-Button 'Reload Agents'
    Wait-ForState 'selected schedule after Refresh and Reload Agents' { Test-SelectionActions $true }
    Write-Output 'PASS UI with selection: Schedule, Refresh, then Reload Agents'

    $taskName = "ClaudeAgentScheduler-$($jobFolder.Name)"
    $toggleCondition = New-Object Windows.Automation.PropertyCondition `
        ([Windows.Automation.AutomationElement]::ClassNameProperty, 'ToggleSwitch')
    $projectCell = Find-NamedControl $project.FullName
    if (-not $projectCell) { throw 'Test project was not found in the schedule list.' }
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $row = $walker.GetParent($projectCell)
    while ($row -and $row.Current.ControlType -ne [Windows.Automation.ControlType]::ListItem) {
        $row = $walker.GetParent($row)
    }
    if (-not $row) { throw 'Schedule row for the test project was not found.' }
    $toggle = $row.FindFirst([Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    if (-not $toggle) { throw 'Schedule toggle for the test project was not found.' }
    $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-ForState 'task disabled' { (Get-ScheduledTask -TaskName $taskName -TaskPath '\').State -eq 'Disabled' }
    Invoke-Button 'Refresh'
    Wait-ForState 'disabled task remains selected and displays Off' {
        (Test-SelectionActions $true) -and (Find-NamedControl ('fixture-agent {0} Off' -f [char]183))
    }
    Write-Output 'PASS UI with disabled task: toggle Off then Refresh'

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
    Invoke-Button 'Refresh'
    Wait-ForState 'canceled row absent after Refresh' { (Test-SelectionActions $false) -and !(Find-NamedControl $project.FullName) }
    Write-Output 'PASS UI cancellation: Cancel disabled task, then Refresh'

    Invoke-Button 'Schedule'
    Wait-ForState 'new schedule registered after cancellation' { @(Find-TestJob).Count -eq 2 -and (Test-SelectionActions $true) }
    $newJob = Find-TestJob | Where-Object Name -ne $jobFolder.Name | Select-Object -First 1
    $worker = Start-Process -FilePath $Executable -ArgumentList '--run', $newJob.Name -PassThru
    try {
        if (!$worker.WaitForExit(10000)) { $worker.Kill(); $worker.WaitForExit(); throw 'Fixture run timed out.' }
        if ($worker.ExitCode -ne 0) { throw "Fixture returned exit code $($worker.ExitCode)." }
    }
    finally { $worker.Dispose() }
    Invoke-Button 'Refresh'
    Wait-ForState 'last run result displayed for the new selection' {
        (Test-SelectionActions $true) -and (Find-NamedControl ('fixture-agent {0} Last run: Completed' -f [char]183))
    }
    Invoke-Button 'Cancel Schedule'
    Wait-ForState 'completed schedule canceled with log preserved' {
        (Test-SelectionActions $false) -and (Test-Path -LiteralPath (Join-Path $newJob.FullName 'canceled')) -and
            @(Get-ChildItem -LiteralPath $newJob.FullName -Filter 'run-*.log' -File).Count -gt 0
    }
    Write-Output 'PASS UI rescheduling: Schedule again, run fixture, Refresh, then Cancel'
}
finally {
    if ($process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
    if ($damagedJob) {
        [IO.File]::Delete((Join-Path $damagedJob.FullName 'job.xml'))
        [IO.Directory]::Delete($damagedJob.FullName)
    }
    foreach ($jobFolder in (Find-TestJob | Where-Object Name -Match '^[a-f0-9]{32}$')) {
        $task = Get-ScheduledTask -TaskPath '\' -TaskName "ClaudeAgentScheduler-$($jobFolder.Name)" -ErrorAction SilentlyContinue
        if ($task) { $task | Unregister-ScheduledTask -Confirm:$false }
        [IO.Directory]::Delete($jobFolder.FullName, $true)
    }
}
