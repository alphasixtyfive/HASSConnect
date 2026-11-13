param(
    [string]$AppPath = 'artifacts/app/HassConnect.App.exe',
    [string]$OutputDirectory = 'artifacts/ui-smoke'
)

# Run with Windows PowerShell for the Windows UI Automation assemblies.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$workspace = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory))
$testProfile = Join-Path $output ('profile-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testProfile) | Out-Null
$settingsPath = Join-Path $testProfile 'settings.json'
@{ SchemaVersion=1; DeviceId='ui-test'; DeviceName='Desktop'; ServerUrl=''; Theme='Dark';
   ShareSensors=$false; EnabledSensors=@('cpu_usage'); PcControlEnabled=$false } |
    ConvertTo-Json | Set-Content -LiteralPath $settingsPath
$previousProfile = $env:HASSCONNECT_TEST_PROFILE
$env:HASSCONNECT_TEST_PROFILE = $testProfile
$appProcess = $null

function Find-Control([string]$id) {
    $script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Wait-Control([string]$id) {
    for ($i=0; $i -lt 50; $i++) {
        $control = Find-Control $id
        if ($null -ne $control) { return $control }
        Start-Sleep -Milliseconds 100
    }
    throw "Control did not appear: $id"
}
function Wait-Closed([string]$id) {
    for ($i=0; $i -lt 50; $i++) {
        if ($null -eq (Find-Control $id)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Dialog did not close: $id"
}
function Invoke-Control([string]$id) {
    (Wait-Control $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Value([string]$id, [string]$value) {
    (Wait-Control $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
function Read-Value([string]$id) {
    (Wait-Control $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Capture([string]$name) {
    & (Join-Path $workspace 'tools/Capture-HassConnectWindow.ps1') -ProcessId $appProcess.Id -OutputPath (Join-Path $output $name) | Out-Null
}

try {
    $appProcess = Start-Process -FilePath (Join-Path $workspace $AppPath) -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 80; $i++) {
        $script:root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'HASS Connect')))
        if ($null -ne $root) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $root) { throw 'The app did not create its window.' }
    (Wait-Control 'ControlsNav').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Control 'AddCustomCommandButton'
    Set-Value 'CustomCommandNameBox' 'Open File Explorer'
    Set-Value 'CustomCommandExecutableBox' (Join-Path $env:WINDIR 'explorer.exe')
    if ((Read-Value 'CustomCommandIdBox') -ne 'command_custom_open_file_explorer') { throw 'Command ID suggestion failed.' }
    Capture 'command-rounded.png'

    Set-Value 'CustomCommandExecutableBox' 'C:\Scripts\action.ps1'
    $arguments = Find-Control 'CustomCommandArgumentsBox'
    if ($null -ne $arguments -and -not $arguments.Current.IsOffscreen) { throw 'Script arguments are still visible.' }
    Set-Value 'CustomCommandExecutableBox' (Join-Path $env:WINDIR 'explorer.exe')

    $blockedWrite = Join-Path $testProfile 'settings.json.tmp'
    [IO.Directory]::CreateDirectory($blockedWrite) | Out-Null
    Invoke-Control 'SaveCustomCommandButton'
    $errorText = Wait-Control 'CustomCommandError'
    if ($errorText.Current.Name -notlike '*denied access*') { throw 'Storage failure was not explained.' }
    if ((Read-Value 'CustomCommandNameBox') -ne 'Open File Explorer') { throw 'Failed save lost the command form.' }
    if (-not (Wait-Control 'SaveCustomCommandButton').Current.IsEnabled) { throw 'Failed save left buttons disabled.' }
    [IO.Directory]::Delete($blockedWrite) # This exact empty test directory was created above.
    Invoke-Control 'SaveCustomCommandButton'
    Wait-Closed 'CustomCommandNameBox'
    Start-Sleep -Milliseconds 300 # Let the native modal close transition restore page input.
    $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if (@($saved.CustomCommands).Count -ne 1) { throw 'Retry did not save exactly one command.' }
    Capture 'controls.png'

    (Wait-Control 'QuickAccessNav').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    Capture 'quick-access.png'

    (Wait-Control 'SensorsNav').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $cpu = $null
    for ($i=0; $i -lt 50; $i++) {
        $cpu = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'Switch'),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'CPU usage')))
        if ($null -ne $cpu -and $cpu.Current.IsEnabled) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $cpu -or -not $cpu.Current.IsEnabled) { throw 'Paused offline sensor choices are disabled.' }
    $cpu.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 300
    $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($saved.EnabledSensors -contains 'cpu_usage' -or $saved.PendingSensorChanges.cpu_usage -ne $false) {
        throw 'Offline sensor choice was not persisted.'
    }
    Capture 'sensors-paused.png'

    (Wait-Control 'ControlsNav').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Control 'AddCustomCommandButton'
    $root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1100, 760)
    Start-Sleep -Milliseconds 600
    $scroll = Wait-Control 'EditorScroll'
    $scrollPattern = $scroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($scrollPattern.Current.VerticallyScrollable) { $scrollPattern.SetScrollPercent(-1, 100) }
    Start-Sleep -Milliseconds 300
    $cancel = Wait-Control 'CancelCustomCommandButton'
    Capture 'command-short.png'
    if ($cancel.Current.IsOffscreen) { throw 'Dialog actions cannot be reached in a short window.' }
    Invoke-Control 'CancelCustomCommandButton'
    Write-Host 'UI checks passed: rounded dialog capture, generated ID, script fields, denied-save recovery, offline preferences, short-window scrolling.'
}
finally {
    if ($null -ne $appProcess) {
        $appProcess.Refresh()
        if (-not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    }
    $env:HASSCONNECT_TEST_PROFILE = $previousProfile
}
