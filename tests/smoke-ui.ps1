param([string]$Executable, [string]$Fixture, [string]$Sandbox, [string]$ArtifactDirectory)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if ($ArtifactDirectory -and -not ('AgentFlowWindowEvidence' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowEvidence.cs') -ReferencedAssemblies System.Drawing
}
if (Get-Process -Name AgentFlow -ErrorAction SilentlyContinue) {
    throw 'Close the running AgentFlow window before starting the single-instance UI suite.'
}
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
$mismatchedJob = $null
try {
    $damagedJob = New-Item -ItemType Directory -Path (Join-Path $jobRoot ([guid]::NewGuid().ToString('N'))) -Force
    Set-Content -LiteralPath (Join-Path $damagedJob.FullName 'job.xml') -Value '<Job>'
    $mismatchedJob = New-Item -ItemType Directory -Path (Join-Path $jobRoot ([guid]::NewGuid().ToString('N'))) -Force
    $wrongId = [guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath (Join-Path $mismatchedJob.FullName 'job.xml') -Value "<Job><Id>$wrongId</Id></Job>"
    $startupError = Join-Path $Sandbox 'startup-error.log'
    $process = Start-Process -FilePath $Executable -RedirectStandardError $startupError -PassThru
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) {
            $details = Get-Content -LiteralPath $startupError -Raw
            throw "AgentFlow exited with code $($process.ExitCode). $details"
        }
    } until ($process.MainWindowHandle -ne 0 -or $watch.Elapsed.TotalSeconds -ge 15)
    if ($process.MainWindowHandle -eq 0) { throw 'AgentFlow window did not open.' }
    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    $duplicate = Start-Process -FilePath $Executable -PassThru
    try {
        if (-not $duplicate.WaitForExit(5000)) {
            $duplicate.Kill()
            $duplicate.WaitForExit()
            throw 'A second launch opened another AgentFlow instance.'
        }
        if ($duplicate.ExitCode -ne 0 -or $process.HasExited) {
            throw 'A second launch did not exit cleanly while the original instance stayed open.'
        }
    }
    finally { $duplicate.Dispose() }
    Write-Output 'PASS UI single instance: a second launch exits and leaves the original window open'

    function Find-Button([string]$Name) {
        $id = switch -CaseSensitive ($Name) {
            'New schedule' { 'NewSchedule' }
            'Refresh' { 'RefreshSchedules' }
            'Open Log' { 'OpenRunLog' }
            'Cancel Schedule' { 'CancelSchedule' }
            'Browse Folder' { 'BrowseProject' }
            'Browse File' { 'BrowseClaude' }
            'Reload Agents' { 'ReloadAgents' }
        }
        $condition = if ($id) {
            New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        } else {
            New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty, $Name)
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $matches = $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
            foreach ($match in $matches) {
                if ($match.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { return $match }
            }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 3)
        $visibleButtons = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
            (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::Button))) | ForEach-Object { $_.Current.Name }
        throw "Button not found: $Name. Buttons: $($visibleButtons -join ', '). $((Get-PSCallStack)[1].Location)"
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

    function Open-Editor {
        Invoke-Button 'New schedule'
        Wait-ForState 'schedule editor' { Find-NamedControl 'Task Instructions' }
    }

    function Confirm-Cancellation {
        Invoke-Button 'Cancel Schedule'
        Wait-ForState 'cancellation dialog' { Find-NamedControl 'Keep schedule' }
        Invoke-Button 'Cancel schedule'
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

    function Save-Evidence([string]$Name) {
        if ($ArtifactDirectory) {
            [AgentFlowWindowEvidence]::Capture($process.MainWindowHandle, (Join-Path $ArtifactDirectory "$Name.png"))
        }
    }

    function Select-Item([string]$Name) {
        $condition = New-Object Windows.Automation.PropertyCondition `
            ([Windows.Automation.AutomationElement]::NameProperty, $Name)
        foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            $pattern = $null
            if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                $pattern.Select()
                return
            }
        }
        throw "Selectable item not found: $Name"
    }

    function Set-Field([string]$Id, [string]$Value) {
        $condition = New-Object Windows.Automation.PropertyCondition `
            ([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
        foreach ($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($element.Current.ControlType -ne [Windows.Automation.ControlType]::Edit) { continue }
            $pattern = $null
            if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern) -and
                -not $pattern.Current.IsReadOnly) {
                try { $pattern.SetValue($Value) }
                catch { throw "Could not set '$Id': $_" }
                Wait-ForState "value committed for $Id" { $pattern.Current.Value -ceq $Value }
                return
            }
        }
        throw "Editable field not found: $Id"
    }

    foreach ($name in 'New schedule', 'Refresh') {
        if (-not (Find-Button $name).Current.IsEnabled) { throw "Expected enabled button: $name" }
    }
    foreach ($name in 'Open Log', 'Cancel Schedule') {
        if ((Find-Button $name).Current.IsEnabled) { throw "Expected disabled button before selection: $name" }
    }
    Invoke-Button 'Refresh'
    Wait-ForState 'disabled selection actions after Refresh' { Test-SelectionActions $false }
    Open-Editor
    foreach ($name in 'Browse Folder', 'Browse File', 'Reload Agents', 'Schedule') {
        if ((Find-Button $name).Current.IsEnabled -eq $false) { throw "Expected enabled button: $name" }
    }
    Invoke-Button 'Reload Agents'
    Write-Output 'PASS UI without selection: Refresh then Reload Agents'
    Invoke-Button 'Schedule'
    Wait-ForState 'empty project validation' { Find-NamedControl 'Select a project folder.' }
    if (Find-TestJob) { throw 'Invalid input created a schedule.' }
    Save-Evidence '01-invalid-schedule'
    Write-Output 'PASS UI validation: empty project keeps the editor open without creating a schedule'
    Set-Field 'ScheduleProject' $project.FullName
    Set-Field 'ScheduleClaude' $Fixture
    Invoke-Button 'Reload Agents'
    Set-Field 'ScheduleInstructions' 'scheduled through the UI'
    Set-Field 'ScheduleAgentInput' 'fixture-agent'
    Invoke-Button 'Schedule'
    $watch.Restart()
    do {
        Start-Sleep -Milliseconds 100
        $jobFolder = Find-TestJob
    } until (($jobFolder -and (Find-Button 'Cancel Schedule').Current.IsEnabled) -or $watch.Elapsed.TotalSeconds -ge 10)
    if (-not $jobFolder -or -not (Find-Button 'Cancel Schedule').Current.IsEnabled) {
        $controls = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $details = foreach ($control in $controls) { if ($control.Current.Name) { $control.Current.Name } }
        throw ('UI did not register and select the schedule. Visible controls: ' + ($details -join ', '))
    }
    if (-not $process.WaitForInputIdle(5000)) { throw 'UI did not finish updating after registration.' }
    Invoke-Button 'Refresh'
    Open-Editor
    Invoke-Button 'Reload Agents'
    Invoke-Button 'Close'
    Wait-ForState 'selected schedule after Refresh and Reload Agents' { Test-SelectionActions $true }
    Write-Output 'PASS UI with selection: Schedule, Refresh, then Reload Agents'

    if ($ArtifactDirectory) {
        $scale = [AgentFlowWindowEvidence]::GetDpiForWindow($process.MainWindowHandle) / 96.0
        if ($scale -le 0) { throw 'Could not determine the test window DPI.' }
        $width = [int](740 * $scale)
        $height = [int](530 * $scale)
        [AgentFlowWindowEvidence]::Resize($process.MainWindowHandle, $width, $height)
        Wait-ForState 'minimum window width' { [AgentFlowWindowEvidence]::Width($process.MainWindowHandle) -eq $width }
        foreach ($name in 'New schedule', 'Refresh', 'Open Log', 'Cancel Schedule') {
            $button = Find-Button $name
            $bounds = $button.Current.BoundingRectangle
            $windowBounds = $window.Current.BoundingRectangle
            if ($button.Current.IsOffscreen -or !$button.Current.Name -or $bounds.IsEmpty -or
                $bounds.Left -lt $windowBounds.Left -or $bounds.Right -gt $windowBounds.Right -or
                $bounds.Top -lt $windowBounds.Top -or $bounds.Bottom -gt $windowBounds.Bottom) {
                throw "Missing accessible name or clipped button at minimum width: $name"
            }
        }
        Save-Evidence '02-minimum-schedules'
        Write-Output 'PASS UI minimum width: primary and selection actions are visible and named'
        [AgentFlowWindowEvidence]::Resize($process.MainWindowHandle, [int](1160 * $scale), [int](730 * $scale))
    }

    $taskName = "ClaudeAgentScheduler-$($jobFolder.Name)"
    $toggleCondition = New-Object Windows.Automation.PropertyCondition `
        ([Windows.Automation.AutomationElement]::AutomationIdProperty, "EnableSchedule-$($jobFolder.Name)")
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
    Select-Item 'Enabled'
    Wait-ForState 'disabled task hidden by Enabled filter' { !(Find-NamedControl $project.FullName) }
    Select-Item 'Paused'
    Wait-ForState 'disabled task shown by Paused filter' { Find-NamedControl $project.FullName }
    $projectCell = Find-NamedControl $project.FullName
    $row = $walker.GetParent($projectCell)
    while ($row -and $row.Current.ControlType -ne [Windows.Automation.ControlType]::ListItem) {
        $row = $walker.GetParent($row)
    }
    $row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output 'PASS UI with disabled task: toggle Off then Refresh'

    Confirm-Cancellation
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

    Open-Editor
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
    $lastRun = Get-ChildItem -LiteralPath $newJob.FullName -Filter 'run-*.log' -File | Select-Object -First 1
    $dayLabel = if ($lastRun.LastWriteTime.Date -eq [DateTime]::Today) { 'Today' } else { 'Yesterday' }
    Wait-ForState 'fixture log timestamp displayed instead of the scheduler placeholder' {
        Find-NamedControl ('{0}, {1}' -f $dayLabel, $lastRun.LastWriteTime.ToString('HH:mm'))
    }
    Select-Item 'Run history'
    Wait-ForState 'completed run in Run history' {
        (Find-NamedControl 'Latest available log for each schedule.') -and (Find-NamedControl $project.FullName)
    }
    Select-Item 'Schedules'
    Wait-ForState 'return to selected schedule' { Test-SelectionActions $true }
    Confirm-Cancellation
    Wait-ForState 'completed schedule canceled with log preserved' {
        (Test-SelectionActions $false) -and (Test-Path -LiteralPath (Join-Path $newJob.FullName 'canceled')) -and
            @(Get-ChildItem -LiteralPath $newJob.FullName -Filter 'run-*.log' -File).Count -gt 0
    }
    Select-Item 'Run history'
    Wait-ForState 'canceled schedule log remains accessible in Run history' { Find-NamedControl $project.FullName }
    $projectCell = Find-NamedControl $project.FullName
    $row = $walker.GetParent($projectCell)
    while ($row -and $row.Current.ControlType -ne [Windows.Automation.ControlType]::ListItem) {
        $row = $walker.GetParent($row)
    }
    if (-not $row) { throw 'Canceled schedule row was not found in Run history.' }
    $row.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-ForState 'canceled run log can be opened' { (Find-Button 'Open Log').Current.IsEnabled }
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
    foreach ($invalidJob in @($damagedJob, $mismatchedJob)) {
        if (-not $invalidJob) { continue }
        [IO.File]::Delete((Join-Path $invalidJob.FullName 'job.xml'))
        [IO.Directory]::Delete($invalidJob.FullName)
    }
    foreach ($jobFolder in (Find-TestJob | Where-Object Name -Match '^[a-f0-9]{32}$')) {
        $task = Get-ScheduledTask -TaskPath '\' -TaskName "ClaudeAgentScheduler-$($jobFolder.Name)" -ErrorAction SilentlyContinue
        if ($task) { $task | Unregister-ScheduledTask -Confirm:$false }
        [IO.Directory]::Delete($jobFolder.FullName, $true)
    }
}
