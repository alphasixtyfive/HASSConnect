$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot '../tools/Get-ReleaseVersion.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('hassconnect-version-' + [guid]::NewGuid() + '.props')
function Set-Version([string]$version) {
    Set-Content -LiteralPath $fixture "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>"
}
function Assert-Rejected([scriptblock]$action) {
    $rejected = $false
    try { & $action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'An invalid release configuration was accepted.' }
}
try {
    foreach ($version in '0.1.0', '1.10.2', '255.255.65535') {
        Set-Version $version
        if ((& $check -PropertiesPath $fixture -Tag "v$version") -cne $version) { throw 'Version changed during validation.' }
    }
    foreach ($version in '1.2', '01.2.3', '1.2.3-beta', '1.2.3+commit', '1.2.3.4', '256.0.0', '0.256.0', '0.0.65536') {
        Set-Version $version
        Assert-Rejected { & $check -PropertiesPath $fixture }
    }
    Set-Version '1.2.3'
    foreach ($tag in 'v1.2.4', 'V1.2.3', '1.2.3', 'vv1.2.3') {
        Assert-Rejected { & $check -PropertiesPath $fixture -Tag $tag }
    }
    'Release version checks passed.'
}
finally { Remove-Item -LiteralPath $fixture -ErrorAction SilentlyContinue }
