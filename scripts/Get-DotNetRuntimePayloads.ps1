[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules'
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
$ProgressPreference = 'SilentlyContinue'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$feedUri = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$allowedHost = 'builds.dotnet.microsoft.com'
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.Directory]::CreateDirectory($output) | Out-Null

function Get-Sha512([string]$path) {
    $algorithm = [Security.Cryptography.SHA512]::Create()
    $stream = [IO.File]::OpenRead($path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

$feedResponse = Invoke-WebRequest -Uri $feedUri -UseBasicParsing -TimeoutSec 60 -MaximumRedirection 3
if ($feedResponse.BaseResponse.ResponseUri.Scheme -ne 'https' -or $feedResponse.BaseResponse.ResponseUri.Host -ne $allowedHost -or $feedResponse.RawContentLength -gt 30000000) { throw 'The official .NET release metadata response is invalid.' }
$feed = $feedResponse.Content | ConvertFrom-Json
if ($feed.'channel-version' -ne '10.0' -or $feed.'release-type' -ne 'lts') { throw 'The official .NET release channel is not the expected LTS channel.' }

$specifications = @(
    @{ Name='desktop'; Section='windowsdesktop'; Asset='windowsdesktop-runtime-win-x86.exe'; Framework='Microsoft.WindowsDesktop.App' },
    @{ Name='aspnet'; Section='aspnetcore-runtime'; Asset='aspnetcore-runtime-win-x86.exe'; Framework='Microsoft.AspNetCore.App' }
)
$records = [Collections.Generic.List[object]]::new()
$stableReleases = @($feed.releases | Where-Object { $_.'release-version' -match '^10\.0\.[0-9]+$' } | Sort-Object { [version]$_.'release-version' } -Descending)
foreach ($spec in $specifications) {
    $candidate = $null
    foreach ($release in $stableReleases) {
        $section = $release.($spec.Section)
        if ($null -eq $section -or $section.version -notmatch '^10\.0\.[0-9]+$') { continue }
        $asset = $section.files | Where-Object { $_.name -eq $spec.Asset -and $_.rid -eq 'win-x86' } | Select-Object -First 1
        if ($null -ne $asset) { $candidate = [pscustomobject]@{ Version=$section.version; Asset=$asset }; break }
    }
    if ($null -eq $candidate -or $candidate.Asset.url -notmatch '^https://builds\.dotnet\.microsoft\.com/' -or $candidate.Asset.hash -notmatch '^[a-fA-F0-9]{128}$') { throw "No official x86 .NET 10 installer was found for $($spec.Name)." }

    $path = Join-Path $output $candidate.Asset.name
    $actualHash = if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Sha512 $path } else { '' }
    if ($actualHash -ne $candidate.Asset.hash.ToLowerInvariant()) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        Invoke-WebRequest -Uri $candidate.Asset.url -OutFile $path -UseBasicParsing -TimeoutSec 300 -MaximumRedirection 3
        $actualHash = Get-Sha512 $path
    }
    if ($actualHash -ne $candidate.Asset.hash.ToLowerInvariant()) { Remove-Item -LiteralPath $path -Force; throw "SHA-512 validation failed for $($candidate.Asset.name)." }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt 1000000 -or $item.Length -gt 300000000) { throw "Installer size is invalid for $($candidate.Asset.name)." }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notlike '*O=Microsoft Corporation*') {
        throw "Microsoft Authenticode validation failed for $($candidate.Asset.name)."
    }
    $records.Add([pscustomobject][ordered]@{
        package = $spec.Name
        framework = $spec.Framework
        version = $candidate.Version
        architecture = 'x86'
        source = $candidate.Asset.url
        filename = $candidate.Asset.name
        size = $item.Length
        sha512 = $actualHash
        signer = $signature.SignerCertificate.Thumbprint
    })
}

$manifest = [pscustomobject][ordered]@{
    schemaVersion = 1
    channel = '10.0 LTS'
    architecture = 'x86'
    retrievedAtUtc = [DateTime]::UtcNow.ToString('O')
    packages = $records.ToArray()
}
$manifestPath = Join-Path $output 'dotnet-runtimes.json'
$temporaryPath = $manifestPath + '.tmp'
[IO.File]::WriteAllText($temporaryPath, ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine, $utf8)
Move-Item -LiteralPath $temporaryPath -Destination $manifestPath -Force
Write-Output "Validated x86 .NET 10 runtime payloads: $($records.Count) packages."
