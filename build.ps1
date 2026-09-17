param(
    [string]$DotNetPath = 'dotnet',
    [string]$OutputDirectory = 'artifacts/app'
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    foreach ($bundledRuntime in 'coreclr.dll', 'Microsoft.UI.Xaml.dll') {
        if (Test-Path -LiteralPath (Join-Path $OutputDirectory $bundledRuntime)) {
            throw 'The output folder contains an old self-contained build. Choose an empty output folder.'
        }
    }
    & ./tools/Get-ReleaseVersion.ps1 | Out-Null
    & ./tests/ReleaseChecks.Tests.ps1
    & $DotNetPath test tests/HassConnect.Tests -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & $DotNetPath publish src/HassConnect.App -c Release --self-contained false -o $OutputDirectory -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    & ./tools/Get-ReleaseVersion.ps1 -AppDirectory $OutputDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $OutputDirectory 'HassConnect.App.pri') -Destination (Join-Path $OutputDirectory 'resources.pri') -Force
    foreach ($resource in 'HassConnect.App.exe', 'HassConnect.App.pri', 'resources.pri', 'App.xbf', 'MainWindow.xbf',
        'Views/SettingsRow.xbf', 'Views/SensorSettingsView.xbf', 'Views/NotificationSettingsView.xbf',
        'Views/AboutView.xbf', 'Assets/app.ico', 'Assets/hass-connect.png') {
        if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $resource))) { throw "Published app is missing $resource." }
    }
    Write-Host "Ready: $OutputDirectory/HassConnect.App.exe"
}
finally { Pop-Location }
