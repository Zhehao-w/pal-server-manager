param([Parameter(Mandatory = $true)][string]$Tag)

$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)$') {
    throw "Unsupported release tag: $Tag"
}
$version = $Matches.version
$coreSource = Get-Content -LiteralPath 'PalServerManager.Core/Models/ManagerProduct.cs' -Raw
$coreMatch = [regex]::Match($coreSource, 'public const string Version\s*=\s*"([^"]+)"')
if (-not $coreMatch.Success -or $coreMatch.Groups[1].Value -cne $version) {
    throw "ManagerProduct.Version must equal $version"
}
[xml]$project = Get-Content -LiteralPath 'PalServerManager.WinUI/PalServerManager.WinUI.csproj' -Raw
$expectedFileVersion = ($version -split '-', 2)[0] + '.0'
$expected = @{
    Version = $version
    InformationalVersion = $version
    AssemblyVersion = $expectedFileVersion
    FileVersion = $expectedFileVersion
}
foreach ($name in $expected.Keys) {
    $actual = [string]$project.Project.PropertyGroup.$name
    if ($actual -cne $expected[$name]) {
        throw "$name is '$actual'; expected '$($expected[$name])' for $Tag"
    }
}
Write-Host "Source versions match $Tag"
