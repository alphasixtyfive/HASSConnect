param(
    [string]$PropertiesPath = (Join-Path $PSScriptRoot '../Directory.Build.props'),
    [string]$Tag,
    [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
[xml]$properties = Get-Content -LiteralPath $PropertiesPath
$version = $properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw 'Version must use major.minor.patch without a prefix or suffix.'
}
$parsed = [version]$version
if ($parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) {
    throw 'Version exceeds Windows Installer limits (255.255.65535).'
}
if ($Tag -and $Tag -cne "v$version") { throw "Release tag '$Tag' does not match v$version." }
if ($AppDirectory) {
    foreach ($name in 'HassConnect.App.exe', 'HassConnect.Core.dll', 'HassConnect.Updates.dll') {
        $file = Get-Item -LiteralPath (Join-Path $AppDirectory $name)
        if ($file.VersionInfo.ProductVersion.Split('+', 2)[0] -cne $version) {
            throw "$name does not match version $version. Rebuild before packaging."
        }
    }
}
return $version
