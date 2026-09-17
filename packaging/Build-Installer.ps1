param(
    [string]$DotNetPath = 'dotnet',
    [string]$AppDirectory = 'artifacts/app',
    [string]$OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    $app = (Resolve-Path -LiteralPath $AppDirectory).ProviderPath
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $output = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
    $version = & ./tools/Get-ReleaseVersion.ps1 -AppDirectory $app

    $dependencies = Get-Content -LiteralPath "$PSScriptRoot/RuntimeDependencies.json" -Raw | ConvertFrom-Json
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $app 'HassConnect.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or
        [version]$dependencies.DotNet.Version -lt [version]$runtimeConfig.runtimeOptions.framework.version) {
        throw 'The installer must provide the .NET runtime required by the published app.'
    }
    [xml]$appProject = Get-Content -LiteralPath "$root/src/HassConnect.App/HassConnect.App.csproj"
    if ($appProject.Project.PropertyGroup.WindowsAppSdkVersion -ne $dependencies.WindowsAppRuntime.Version) {
        throw 'The Windows App Runtime dependency must match the app SDK version.'
    }
    $dependencyArguments = foreach ($name in 'DotNet', 'WindowsAppRuntime', 'VisualCpp') {
        $dependency = $dependencies.$name
        foreach ($field in 'Version', 'Url', 'Sha512', 'Size') {
            "-p:${name}${field}=$($dependency.$field)"
        }
    }

    # Per-user MSI components use registry key paths, including harvested files.
    $ns = 'http://wixtoolset.org/schemas/v4/wxs'
    $document = [xml]"<Wix xmlns='$ns'><Fragment><DirectoryRef Id='INSTALLFOLDER'/><ComponentGroup Id='ApplicationFiles'/></Fragment></Wix>"
    $directoryRoot = $document.DocumentElement.FirstChild.FirstChild
    $group = $directoryRoot.NextSibling
    $directories = @{ '' = $directoryRoot }
    $cleanedDirectories = [Collections.Generic.HashSet[string]]::new()
    function Add-Node($parent, $name, $attributes) {
        $node = $document.CreateElement($name, $ns)
        foreach ($key in $attributes.Keys) { $node.SetAttribute($key, [string]$attributes[$key]) }
        $parent.AppendChild($node) | Out-Null
        return $node
    }
    function Stable-Id([string]$path) {
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant()))).Substring(0, 32)
    }
    foreach ($file in Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($app, $file.FullName)
        if ($file.Extension -in '.pdb', '.log', '.cer', '.pfx' -or $relative -match '(^|\\)(TestProfile|DemoProfile|NotificationImages)(\\|$)' -or $file.Name -in 'settings.json', 'connection.dat') {
            throw "Development or private data in app payload: $relative"
        }
        $folder = [IO.Path]::GetDirectoryName($relative)
        $parent = $directoryRoot
        $path = ''
        foreach ($part in ($folder -split '\\' | Where-Object { $_ })) {
            $path = if ($path) { "$path\$part" } else { $part }
            if (-not $directories.ContainsKey($path)) { $directories[$path] = Add-Node $parent 'Directory' @{ Id = 'D_' + (Stable-Id $path); Name = $part } }
            $parent = $directories[$path]
        }
        $id = Stable-Id $relative
        $component = Add-Node $parent 'Component' @{ Id = "C_$id"; Guid = ([guid]::ParseExact($id, 'N')).ToString() }
        Add-Node $component 'File' @{ Id = "F_$id"; Source = $file.FullName } | Out-Null
        Add-Node $component 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\HASSConnect\Installer\Files'; Name = $id; Type = 'integer'; Value = '1'; KeyPath = 'yes' } | Out-Null
        if ($cleanedDirectories.Add($folder)) { Add-Node $component 'RemoveFolder' @{ Id = "R_$id"; On = 'uninstall' } | Out-Null }
        Add-Node $group 'ComponentRef' @{ Id = "C_$id" } | Out-Null
    }
    foreach ($folder in $directories.Keys) {
        if (-not $cleanedDirectories.Contains($folder)) {
            Add-Node $component 'RemoveFolder' @{ Id = 'R_' + (Stable-Id $folder); Directory = $directories[$folder].GetAttribute('Id'); On = 'uninstall' } | Out-Null
        }
    }
    $generated = Join-Path $output 'ApplicationFiles.wxs'
    $document.Save($generated)
    & $DotNetPath build packaging/HassConnect.Installer.wixproj -c Release "-p:Version=$version" "-p:AppSource=$app" "-p:GeneratedFiles=$generated" -o $output
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    $msi = Join-Path $output "HASSConnect-$version-x64.msi"
    & $DotNetPath build packaging/HassConnect.Setup.wixproj -c Release "-p:Version=$version" "-p:AppSource=$app" "-p:MsiSource=$msi" @dependencyArguments -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
    $setup = Join-Path $output "HASSConnect-$version-Setup-x64.exe"
    "$((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))" |
        Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Ready: $setup"
}
finally { Pop-Location }
