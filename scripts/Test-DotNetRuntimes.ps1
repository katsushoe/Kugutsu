[CmdletBinding()]
param([string]$ReportPath)

$ErrorActionPreference = 'Stop'
$required = @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App','Microsoft.AspNetCore.App')
$rootKey = $null

try {
    $rootKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry32)
    $installKey = $rootKey.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x86')
    if ($null -eq $installKey) { throw 'The x86 .NET installation registration is absent.' }
    try { $dotnetRoot = $installKey.GetValue('InstallLocation') -as [string] }
    finally { $installKey.Dispose() }
    if ([string]::IsNullOrWhiteSpace($dotnetRoot) -or -not [IO.Path]::IsPathFullyQualified($dotnetRoot)) {
        throw 'The x86 .NET installation path is invalid.'
    }

    $installed = [ordered]@{}
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($framework in $required) {
        $frameworkKey = $rootKey.OpenSubKey("SOFTWARE\dotnet\Setup\InstalledVersions\x86\sharedfx\$framework")
        $versions = [Collections.Generic.List[string]]::new()
        if ($null -ne $frameworkKey) {
            try {
                foreach ($name in $frameworkKey.GetValueNames()) {
                    $parsed = $null
                    if ([version]::TryParse($name, [ref]$parsed) -and $parsed.Major -eq 10 -and $parsed.Minor -eq 0) {
                        $runtimeDirectory = Join-Path $dotnetRoot "shared\$framework\$name"
                        if ([IO.Directory]::Exists($runtimeDirectory)) { $versions.Add($name) }
                    }
                }
            }
            finally { $frameworkKey.Dispose() }
        }
        $installed[$framework] = @($versions | Sort-Object { [version]$_ } -Descending)
        if ($versions.Count -eq 0) { $missing.Add($framework) }
    }

    $report = [ordered]@{
        schemaVersion = 1
        architecture = 'x86'
        dotnetRoot = $dotnetRoot
        requiredFrameworks = $required
        installedVersions = $installed
        missingFrameworks = $missing.ToArray()
        satisfied = ($missing.Count -eq 0)
    }
    $json = $report | ConvertTo-Json -Depth 6
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $fullReportPath = [IO.Path]::GetFullPath($ReportPath)
        $parent = Split-Path -Parent $fullReportPath
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $temporaryPath = $fullReportPath + '.tmp'
        [IO.File]::WriteAllText($temporaryPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $fullReportPath -Force
    }
    [Console]::Out.WriteLine($json)
    if ($missing.Count -gt 0) { exit 1 }
    exit 0
}
catch {
    [Console]::Out.WriteLine('{"schemaVersion":1,"architecture":"x86","satisfied":false,"error":"detection_failed"}')
    exit 2
}
finally {
    if ($null -ne $rootKey) { $rootKey.Dispose() }
}
