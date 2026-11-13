param([string]$ReleaseDirectory = 'artifacts/release')

# This test installs and removes the app in a disposable GitHub Windows runner.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Run installer smoke checks only in a disposable GitHub Actions runner.' }
$version = & "$PSScriptRoot/../tools/Get-ReleaseVersion.ps1"
$setup = (Resolve-Path "$ReleaseDirectory/HASSConnect-$version-Setup-x64.exe").ProviderPath
$app = Join-Path $env:LOCALAPPDATA 'Programs/HASSConnect/HassConnect.App.exe'
$data = Join-Path $env:LOCALAPPDATA 'HassConnect'
$settingsPath = Join-Path $data 'settings.json'
$credentialsPath = Join-Path $data 'connection.dat'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'HASS Connect.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Run-Setup([string]$installer, [string]$operation) {
    $process = Start-Process $installer -ArgumentList "$operation /quiet /norestart" -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Setup $operation failed: $($process.ExitCode)" }
}
function Start-InstalledApp {
    if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $shortcut)) {
        throw 'Installation did not create the app and Start-menu shortcut.'
    }
    $process = Start-Process $app -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    $process.Refresh()
    if ($process.HasExited) { throw 'The installed app could not start.' }
    return $process
}
function Assert-Removed($process) {
    $process.Refresh()
    if (-not $process.HasExited) { throw 'Uninstall left the app running.' }
    if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $shortcut)) {
        throw 'Uninstall left the app or Start-menu shortcut.'
    }
    $startup = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
    if ($startup -and $startup.PSObject.Properties['HassConnect']) {
        throw 'Uninstall left the startup entry.'
    }
}

# Fresh installation, launch and removal of the candidate.
Run-Setup $setup '/install'
$running = Start-InstalledApp
Run-Setup $setup '/uninstall'
Assert-Removed $running
Write-Host 'Fresh installation, launch and removal passed.'

# Download the preceding published version only when it is older than the candidate.
$releaseText = & gh release view --json tagName,assets
if ($LASTEXITCODE -ne 0) { throw 'Could not read the preceding release.' }
$previous = $releaseText | ConvertFrom-Json
if ([version]$previous.tagName.TrimStart('v') -ge [version]$version) {
    Write-Host 'Fresh installation checks passed; no older latest release to upgrade.'
    exit 0
}
$previousDirectory = Join-Path $ReleaseDirectory 'previous'
New-Item -ItemType Directory -Path $previousDirectory -Force | Out-Null
& gh release download $previous.tagName --pattern '*-Setup-x64.exe' --pattern 'SHA256SUMS.txt' --dir $previousDirectory
if ($LASTEXITCODE -ne 0) { throw 'Could not download the preceding installer.' }
$previousSetup = @(Get-ChildItem -LiteralPath $previousDirectory -Filter '*-Setup-x64.exe')
if ($previousSetup.Count -ne 1) { throw 'Expected one preceding installer.' }
$expected = (Get-Content -LiteralPath (Join-Path $previousDirectory 'SHA256SUMS.txt') -Raw).Trim()
$actual = (Get-FileHash -LiteralPath $previousSetup[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -cne "$actual  $($previousSetup[0].Name)") { throw 'Preceding installer checksum mismatch.' }
Run-Setup $previousSetup[0].FullName '/install'
Write-Host "Installed preceding release $($previous.tagName)."

# Use synthetic, disconnected data: no real token or Home Assistant server.
New-Item -ItemType Directory -Path $data -Force | Out-Null
@{ SchemaVersion=1; DeviceId='installer-upgrade-test'; DeviceName='Desktop';
   ServerUrl=''; Theme='Dark'; ShareSensors=$false; EnabledSensors=@('cpu_usage');
   PcControlEnabled=$false; NotificationsEnabled=$false } |
    ConvertTo-Json | Set-Content -LiteralPath $settingsPath
$plain = [Text.Encoding]::UTF8.GetBytes('{"AccessToken":"installer-test-token","WebhookId":null}')
$encrypted = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
[IO.File]::WriteAllBytes($credentialsPath, $encrypted)
$credentialsHash = (Get-FileHash -LiteralPath $credentialsPath).Hash
$oldRunning = Start-InstalledApp
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -LiteralPath $runKey -Name HassConnect -Value ('"' + $app + '" --tray')
Run-Setup $setup '/install'
$oldRunning.Refresh()
if (-not $oldRunning.HasExited) { throw 'Upgrade left the preceding app running.' }
if ([version](Get-Item -LiteralPath $app).VersionInfo.ProductVersion.Split('+')[0] -ne [version]$version) {
    throw 'Upgrade installed the wrong app version.'
}
$running = Start-InstalledApp
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if ($settings.DeviceId -ne 'installer-upgrade-test' -or $settings.ShareSensors -or
    $settings.Theme -ne 'Dark' -or $settings.EnabledSensors -notcontains 'cpu_usage') {
    throw 'Upgrade changed saved preferences or device identity.'
}
if ((Get-FileHash -LiteralPath $credentialsPath).Hash -ne $credentialsHash) { throw 'Upgrade changed encrypted credentials.' }
$settingsHash = (Get-FileHash -LiteralPath $settingsPath).Hash
Run-Setup $setup '/uninstall'
Assert-Removed $running
if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $settingsHash -or
    (Get-FileHash -LiteralPath $credentialsPath).Hash -ne $credentialsHash) {
    throw 'Uninstall changed settings or encrypted credentials.'
}
Write-Host 'Installer checks passed: fresh launch, upgrade, retained preferences and credentials, shutdown and removal.'
