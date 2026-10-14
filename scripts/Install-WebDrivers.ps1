[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallationRoot)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath($InstallationRoot)
$config = Join-Path $root 'config\detected-launch-targets.json'
$driversRoot = Join-Path $root 'bin\drivers'
$workRoot = Join-Path $root 'dat\driver-setup'
$manifestPath = Join-Path $driversRoot 'drivers-manifest.json'
$utf8 = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Net.Http
$handler = [Net.Http.HttpClientHandler]::new()
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromMinutes(3)
$allowedHosts = @('googlechromelabs.github.io','storage.googleapis.com','github.com','release-assets.githubusercontent.com','msedgedriver.azureedge.net','msedgedriver.microsoft.com')

function Get-OfficialResponse([string]$uri, [int64]$limit) {
    $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode -or $response.RequestMessage.RequestUri.Scheme -ne 'https' -or
        $response.RequestMessage.RequestUri.Host -notin $allowedHosts) { $response.Dispose(); throw 'Official WebDriver response URI or status is invalid.' }
    if ($response.Content.Headers.ContentLength -gt $limit) { $response.Dispose(); throw 'Official WebDriver response exceeds its size limit.' }
    return $response
}

function Get-Sha256([string]$path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

function Get-Json([string]$uri) {
    $response = Get-OfficialResponse $uri 20000000
    try { return (ConvertFrom-Json ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())) }
    finally { $response.Dispose() }
}

function Save-OfficialDriver([string]$browser, [string]$version, [string]$uri, [string]$entryName, [string]$destination) {
    $archive = Join-Path $workRoot ($browser + '.zip')
    $response = Get-OfficialResponse $uri 150000000
    try {
        $sourceStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.File]::Open($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = [byte[]]::new(65536)
            $total = [int64]0
            while (($count = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $total += $count
                if ($total -gt 150000000) { throw 'WebDriver archive exceeds its size limit.' }
                $output.Write($buffer, 0, $count)
            }
        }
        finally { $sourceStream.Dispose(); $output.Dispose() }
    }
    finally { $response.Dispose() }
    $archiveInfo = Get-Item -LiteralPath $archive
    if ($archiveInfo.Length -lt 1 -or $archiveInfo.Length -gt 150000000) { throw 'WebDriver archive size is invalid.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = @($zip.Entries | Where-Object { $_.FullName -eq $entryName })
        if ($entry.Count -ne 1 -or $entry[0].Length -gt 100000000) { throw 'Expected executable is missing from WebDriver archive.' }
        $input = $entry[0].Open()
        $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output) }
        finally { $input.Dispose(); $output.Dispose() }
    }
    finally { $zip.Dispose(); Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    $publisher = @{chrome='Google LLC';edge='Microsoft Corporation';firefox='Mozilla Corporation'}[$browser]
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notlike "*CN=$publisher*")
    { Remove-Item -LiteralPath $destination -Force; throw 'WebDriver Authenticode publisher is invalid.' }
    return [pscustomobject]@{ browser=$browser; browserVersion=$version; driverVersion=$null; source=$uri; path=$destination; size=(Get-Item $destination).Length; sha256=(Get-Sha256 $destination); signer=$signature.SignerCertificate.Thumbprint }
}

if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { throw 'Detected launch target settings are missing.' }
[void][Reflection.Assembly]::Load('System.IO.Compression.FileSystem')
$detected = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
$appSettings = Get-Content -LiteralPath (Join-Path $root 'config\appsettings.json') -Raw | ConvertFrom-Json
$launchTargets = @($appSettings.Kugutsu.AllowedLaunchTargets) + @($detected.AllowedLaunchTargets)
$browserMappings = @{}
foreach ($name in $detected.BrowserLaunchTargets.PSObject.Properties.Name) { $browserMappings[$name] = $detected.BrowserLaunchTargets.$name }
foreach ($name in $appSettings.Kugutsu.BrowserLaunchTargets.PSObject.Properties.Name) { $browserMappings[$name] = $appSettings.Kugutsu.BrowserLaunchTargets.$name }
$browserPaths = @{}
$records = [Collections.Generic.List[object]]::new()
$platform = if ([Environment]::Is64BitOperatingSystem) { 'win64' } else { 'win32' }
New-Item -ItemType Directory -Path $driversRoot,$workRoot -Force | Out-Null
try {
    foreach ($browser in @('chrome','edge','firefox')) {
        $id = $browserMappings[$browser]
        if (-not $id) { continue }
        $target = $launchTargets | Where-Object { $_.Id -eq $id } | Select-Object -First 1
        if (-not $target -or -not (Test-Path -LiteralPath $target.ExecutablePath -PathType Leaf)) { continue }
        $browserVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($target.ExecutablePath).FileVersion
        $version = [version]($browserVersion -replace '[^0-9.].*$','')
        $destinationDirectory = Join-Path $driversRoot $browser
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        $destination = Join-Path $destinationDirectory (@{chrome='chromedriver.exe';edge='msedgedriver.exe';firefox='geckodriver.exe'}[$browser])
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
        switch ($browser) {
            'chrome' {
                $metadata = Get-Json 'https://googlechromelabs.github.io/chrome-for-testing/latest-patch-versions-per-build-with-downloads.json'
                $build = "$($version.Major).$($version.Minor).$($version.Build)"
                $release = $metadata.builds.$build
                $download = $release.downloads.chromedriver | Where-Object { $_.platform -eq $platform } | Select-Object -First 1
                if (-not $release -or -not $download) { throw "No official ChromeDriver matches Chrome build $build for $platform." }
                $record = Save-OfficialDriver $browser $version.ToString() $download.url "chromedriver-$platform/chromedriver.exe" $destination
                $record.driverVersion = $release.version
            }
            'edge' {
                if ($version.Major -lt 79) { throw 'Installed Edge is older than the supported Chromium release.' }
                $edgeArch = if ($platform -eq 'win64') { 'win64' } else { 'win32' }
                $build = "$($version.Major).$($version.Minor).$($version.Build)"
                $latest = Get-OfficialResponse "https://msedgedriver.microsoft.com/LATEST_RELEASE_$build" 100
                try { $driverVersion = $latest.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim() }
                finally { $latest.Dispose() }
                if ($driverVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $driverVersion -notlike "$build.*") { throw 'Official Edge WebDriver does not match the installed browser build.' }
                $uri = "https://msedgedriver.microsoft.com/$driverVersion/edgedriver_$edgeArch.zip"
                $record = Save-OfficialDriver $browser $version.ToString() $uri 'msedgedriver.exe' $destination
                $record.driverVersion = $driverVersion
            }
            'firefox' {
                if ($version.Major -lt 115) { throw 'Installed Firefox is older than the supported release.' }
                $driverVersion = '0.36.0'
                $zipName = "geckodriver-v$driverVersion-$platform.zip"
                $uri = "https://github.com/mozilla/geckodriver/releases/download/v$driverVersion/$zipName"
                $record = Save-OfficialDriver $browser $version.ToString() $uri 'geckodriver.exe' $destination
                $record.driverVersion = $driverVersion
            }
        }
        $browserPaths[$browser] = $destination
        $records.Add($record)
    }
    if ($records.Count -gt 0) {
        $detected | Add-Member -NotePropertyName BrowserDriverPaths -NotePropertyValue $browserPaths -Force
        $temporary = $config + '.tmp'
        [IO.File]::WriteAllText($temporary, ($detected | ConvertTo-Json -Depth 8), $utf8)
        Move-Item -LiteralPath $temporary -Destination $config -Force
        [IO.File]::WriteAllText($manifestPath, (@{schemaVersion=1;architecture=$platform;drivers=$records.ToArray()} | ConvertTo-Json -Depth 8), $utf8)
    }
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
