param(
    [string]$SurvivalcraftDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$stageRoot = Join-Path $artifactsRoot ".netmod-stage"
$zipPath = Join-Path $artifactsRoot "SurvivalcraftGenius.zip"
$packagePath = Join-Path $artifactsRoot "SurvivalcraftGenius.netmod"
$modRoot = Join-Path (Join-Path $repositoryRoot "src") "SurvivalcraftGenius"
$projectPath = Join-Path $modRoot "SurvivalcraftGenius.csproj"

function Assert-PathWithinArtifacts {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $fullArtifactsRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside artifacts: $fullPath"
    }
}

if ([string]::IsNullOrWhiteSpace($SurvivalcraftDir)) {
    throw "Pass -SurvivalcraftDir pointing at the game DLL directory."
}

$SurvivalcraftDir = [IO.Path]::GetFullPath($SurvivalcraftDir).TrimEnd("\", "/") + [IO.Path]::DirectorySeparatorChar
if (-not (Test-Path -LiteralPath (Join-Path $SurvivalcraftDir "Survivalcraft.dll"))) {
    throw "Survivalcraft.dll was not found in '$SurvivalcraftDir'."
}

& dotnet build $projectPath -c Release "-p:SurvivalcraftDir=$SurvivalcraftDir"
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Assert-PathWithinArtifacts -Path $stageRoot
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot | Out-Null

$buildOutput = Join-Path (Join-Path (Join-Path $modRoot "bin") "Release") "net10.0"
Copy-Item -LiteralPath (Join-Path $buildOutput "SurvivalcraftGenius.dll") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $modRoot "modinfo.json") -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $modRoot "mod.netxdb") -Destination $stageRoot

$assetsSource = Join-Path $modRoot "Assets"
if (Test-Path -LiteralPath $assetsSource) {
    Copy-Item -LiteralPath $assetsSource -Destination $stageRoot -Recurse
}

Assert-PathWithinArtifacts -Path $zipPath
Assert-PathWithinArtifacts -Path $packagePath
foreach ($path in @($zipPath, $packagePath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

Add-Type -AssemblyName System.IO.Compression
$archiveStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
$archive = $null
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $archiveStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false)
    $fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $relativePaths = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $stageRoot -Recurse -File) {
        $relativePath = $file.FullName.Substring($stageRoot.Length).TrimStart("\", "/").Replace("\", "/")
        $relativePaths.Add($relativePath)
    }
    $relativePaths.Sort([StringComparer]::Ordinal)

    foreach ($relativePath in $relativePaths) {
        $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTimestamp
        $inputPath = Join-Path $stageRoot ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
        $inputStream = [IO.File]::OpenRead($inputPath)
        $entryStream = $entry.Open()
        try {
            $inputStream.CopyTo($entryStream)
        }
        finally {
            $entryStream.Dispose()
            $inputStream.Dispose()
        }
    }
}
finally {
    if ($null -ne $archive) {
        $archive.Dispose()
    }
    $archiveStream.Dispose()
}

Move-Item -LiteralPath $zipPath -Destination $packagePath
Remove-Item -LiteralPath $stageRoot -Recurse -Force
Write-Output "NETMOD_BUILT $packagePath"
