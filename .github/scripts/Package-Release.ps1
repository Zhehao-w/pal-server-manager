param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Unsupported release tag: $Tag"
}
$publish = (Resolve-Path -LiteralPath $PublishDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'PalServerManager.WinUI.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $publish 'Helpers/PalServer-KeepAwake.exe') -PathType Leaf)) {
    throw 'Publish output is missing the Manager or KeepAwake executable.'
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$notices = [ordered]@{
    'LICENSE' = Join-Path $repoRoot 'LICENSE'
    'ASSETS.md' = Join-Path $repoRoot 'docs/ASSETS.md'
}
foreach ($source in $notices.Values) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required release notice is missing: $source"
    }
}
$output = [System.IO.Path]::GetFullPath($OutputDir)
if ($output.Equals($publish, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($publish + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output directory must not be inside the publish directory.'
}
[System.IO.Directory]::CreateDirectory($output) | Out-Null
$zipName = "PalServerManager-$Tag-win-x64.zip"
$zipPath = Join-Path $output $zipName
$sumsPath = Join-Path $output 'SHA256SUMS.txt'
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $sumsPath)) {
    throw 'Release output already exists; refusing to overwrite it.'
}

$stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        Get-ChildItem -LiteralPath $publish -Recurse -File |
            Where-Object { $_.Extension -ine '.pdb' } |
            ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/')
                if ($relative -notin $notices.Keys) {
                    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                        $archive, $_.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
                }
            }
        foreach ($entryName in $notices.Keys) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $notices[$entryName], $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }
} finally {
    $stream.Dispose()
}

$check = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($check.Entries | ForEach-Object FullName)
    if ($names -notcontains 'PalServerManager.WinUI.exe' -or
        $names -notcontains 'Helpers/PalServer-KeepAwake.exe' -or
        $names -notcontains 'LICENSE' -or
        $names -notcontains 'ASSETS.md' -or
        @($names | Where-Object { $_ -match '\.pdb$' }).Count -ne 0) {
        throw 'Release ZIP layout verification failed.'
    }
} finally {
    $check.Dispose()
}
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($sumsPath, "$hash  $zipName`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Packaged $zipName with SHA-256 $hash"
