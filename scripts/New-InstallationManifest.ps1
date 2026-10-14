[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BinDirectory
)

$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedBin = (Resolve-Path -LiteralPath $BinDirectory).Path
if (-not (Test-Path -LiteralPath $resolvedBin -PathType Container)) {
    throw 'The bin directory does not exist.'
}

$files = [System.Collections.Generic.List[object]]::new()
function Get-FileRecord([string]$relativePath, [string]$component, [string]$role, [string]$version) {
    $fullPath = Join-Path $resolvedBin ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required installation file is missing: $relativePath"
    }
    $item = Get-Item -LiteralPath $fullPath
    $hash = (Get-Sha256 $fullPath)
    return [pscustomobject][ordered]@{
        component = $component
        role = $role
        path = $relativePath
        version = $version
        size = $item.Length
        sha256 = $hash
    }
}

$components = @(
    @{ Name = 'Kugutsu'; Assembly = 'Kugutsu.dll'; AppHost = 'Kugutsu.exe' },
    @{ Name = 'Kugutsu.McpProxy'; Assembly = 'Kugutsu.McpProxy.dll'; AppHost = 'Kugutsu.McpProxy.exe' },
    @{ Name = 'Kugutsu.ConsoleProbe'; Assembly = 'Kugutsu.ConsoleProbe.dll'; AppHost = 'Kugutsu.ConsoleProbe.exe' },
    @{ Name = 'Kugutsu.AloeHost'; Assembly = 'Kugutsu.AloeHost.dll'; AppHost = 'Kugutsu.AloeHost.exe' }
)
$applicationVersion = $null
foreach ($component in $components) {
    $assemblyPath = Join-Path $resolvedBin $component.Assembly
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Required installation assembly is missing: $($component.Assembly)"
    }
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
    if ($assemblyName.Name -ne $component.Name -or $null -eq $assemblyName.Version) {
        throw "Installation assembly identity is invalid: $($component.Assembly)"
    }
    $version = $assemblyName.Version.ToString()
    if ($component.Name -eq 'Kugutsu') { $applicationVersion = $version }
    $files.Add((Get-FileRecord $component.Assembly $component.Name 'managedAssembly' $version))
    $files.Add((Get-FileRecord $component.AppHost $component.Name 'appHost' $version))
}

$aloeSource = Join-Path $repoRoot 'src\Shared\AloeDependencies.cs'
$source = [IO.File]::ReadAllText($aloeSource)
$bundleMatch = [regex]::Match($source, 'internal const string Bundle = "([0-9]{8})";')
$dependencyMatches = [regex]::Matches($source, 'new\("(?<file>[^"]+)",\s*"(?<assembly>[^"]+)",\s*"(?<version>[0-9.]+)",\s*"(?<sha256>[A-Fa-f0-9]{64})"\)')
if (-not $bundleMatch.Success -or $dependencyMatches.Count -ne 3) {
    throw 'The fixed Aloe dependency manifest could not be read.'
}
$bundle = $bundleMatch.Groups[1].Value
$dependencyNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($match in $dependencyMatches) {
    $dependency = [pscustomobject]@{
        File = $match.Groups['file'].Value
        Assembly = $match.Groups['assembly'].Value
        Version = $match.Groups['version'].Value
        Sha256 = $match.Groups['sha256'].Value.ToUpperInvariant()
    }
    if (-not $dependencyNames.Add($dependency.File)) {
        throw 'The fixed Aloe dependency manifest contains a duplicate file.'
    }
    $relativePath = 'aloe/' + $bundle + '/' + $dependency.File
    $fullPath = Join-Path $resolvedBin ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required installation dependency is missing: $relativePath"
    }
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($fullPath)
    $actualHash = (Get-Sha256 $fullPath)
    if ($assemblyName.Name -ne $dependency.Assembly -or $assemblyName.Version.ToString() -ne $dependency.Version -or $actualHash -ne $dependency.Sha256) {
        throw "Fixed Aloe dependency identity or SHA-256 does not match: $relativePath"
    }
    $files.Add((Get-FileRecord $relativePath 'Aloe' $dependency.Assembly $dependency.Version))
}

$appRoot = Split-Path -Parent $resolvedBin
$dataRoot = Join-Path $appRoot 'dat'
$ocrSource = Join-Path $repoRoot 'src\Kugutsu.App\TesseractLocalOcr.cs'
$ocrText = [IO.File]::ReadAllText($ocrSource)
$modelBlock = [regex]::Match($ocrText, 'Models\s*=\s*\[(?<entries>.*?)\];', [Text.RegularExpressions.RegexOptions]::Singleline)
$libraryBlock = [regex]::Match($ocrText, 'Libraries\s*=\s*\[(?<entries>.*?)\];', [Text.RegularExpressions.RegexOptions]::Singleline)
$modelMatches = [regex]::Matches($modelBlock.Groups['entries'].Value, '\("(?<file>[^"]+)",\s*"(?<sha256>[A-Fa-f0-9]{64})"\)')
$libraryMatches = [regex]::Matches($libraryBlock.Groups['entries'].Value, '\("(?<file>[^"]+)",\s*"(?<sha256>[A-Fa-f0-9]{64})"\)')
$ocrVersion = [regex]::Match($ocrText, 'tessdata_fast-(?<version>[0-9.]+)')
if (-not $modelBlock.Success -or -not $libraryBlock.Success -or $modelMatches.Count -ne 2 -or $libraryMatches.Count -ne 2 -or -not $ocrVersion.Success) {
    throw 'The fixed OCR dependency manifest could not be read.'
}
$ocrVersion = $ocrVersion.Groups['version'].Value
$modelDirectory = "ocr/tessdata_fast-$ocrVersion"
foreach ($model in $modelMatches) {
    $relativePath = "$modelDirectory/$($model.Groups['file'].Value)"
    $fullPath = Join-Path $dataRoot ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf) -or (Get-Sha256 $fullPath) -ne $model.Groups['sha256'].Value.ToUpperInvariant()) {
        throw "Fixed OCR model is missing or has an unexpected SHA-256: $relativePath"
    }
    $files.Add((Get-FileRecord ('../dat/' + $relativePath) 'Tesseract' "traineddata-$ocrVersion" $ocrVersion))
}
foreach ($library in $libraryMatches) {
    $relativePath = 'x86/' + $library.Groups['file'].Value
    $fullPath = Join-Path $resolvedBin ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf) -or (Get-Sha256 $fullPath) -ne $library.Groups['sha256'].Value.ToUpperInvariant()) {
        throw "Fixed OCR native library is missing or has an unexpected SHA-256: $relativePath"
    }
    $files.Add((Get-FileRecord $relativePath 'Tesseract' 'nativeLibrary' '5.2.0'))
}
$licenseRelativePath = "$modelDirectory/LICENSE"
$licensePath = Join-Path $dataRoot ($licenseRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "OCR model license is missing: $licenseRelativePath" }
$files.Add((Get-FileRecord ('../dat/' + $licenseRelativePath) 'Tesseract' 'license' $ocrVersion))

$manifest = [pscustomobject][ordered]@{
    schemaVersion = 1
    product = 'Kugutsu'
    version = $applicationVersion
    architecture = 'x86'
    targetFramework = 'net10.0-windows'
    files = $files.ToArray()
}
$json = $manifest | ConvertTo-Json -Depth 8
$manifestPath = Join-Path $resolvedBin 'installation.json'
$tempPath = $manifestPath + '.tmp'
[IO.File]::WriteAllText($tempPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempPath -Destination $manifestPath -Force
Write-Output "Created $manifestPath with $($files.Count) verified files."
