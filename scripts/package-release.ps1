[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$OutputDir,
    [string]$Version,
    [string]$NuGetPackageRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = [xml](Get-Content -LiteralPath (Join-Path $root 'src\PocketDesk\PocketDesk.csproj') -Raw)
$projectVersion = [string]$project.Project.PropertyGroup.Version
if (-not $Version) { $Version = $projectVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or $Version -ne $projectVersion) {
    throw 'Package version must be a SemVer matching PocketDesk.csproj.'
}
$PublishDir = [IO.Path]::GetFullPath($PublishDir, $root)
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts\releases' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir, $root)
if (-not $NuGetPackageRoot) {
    $NuGetPackageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
}
$NuGetPackageRoot = [IO.Path]::GetFullPath($NuGetPackageRoot, $root)
$archiveName = "PocketDesk-v$Version-win-x64.zip"
$archivePath = Join-Path $OutputDir $archiveName
$checksumPath = Join-Path $OutputDir 'SHA256SUMS'
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) {
    throw 'Release outputs already exist. Choose a new -OutputDir; existing assets are never overwritten.'
}

# Exact runtime allowlist from the SDK dependency manifest, rather than copying a publish directory wholesale.
# Only these executable/runtime asset sections and the three app host/config files may enter the ZIP.
$depsPath = Join-Path $PublishDir 'PocketDesk.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
if ($deps.runtimeTarget.name -notmatch '/win-x64$') { throw 'Expected a self-contained win-x64 publish.' }
$target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
if (-not $target.PSObject.Properties["PocketDesk/$Version"]) { throw 'Published application version does not match the release.' }
$runtimeFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('PocketDesk.exe', 'PocketDesk.deps.json', 'PocketDesk.runtimeconfig.json')) { [void]$runtimeFiles.Add($name) }
foreach ($library in $target.PSObject.Properties) {
    foreach ($sectionName in @('runtime', 'native', 'resources')) {
        $section = $library.Value.PSObject.Properties[$sectionName]
        if ($null -eq $section) { continue }
        foreach ($asset in $section.Value.PSObject.Properties) {
            $relative = $asset.Name.Replace('\', '/')
            if ($relative -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))' -or [IO.Path]::GetExtension($relative) -notin @('.dll', '.exe')) {
                throw "Unexpected runtime asset in dependency manifest: $relative"
            }
            [void]$runtimeFiles.Add($relative)
        }
    }
}

$files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $runtimeFiles) { $files.Add($relative, (Join-Path $PublishDir $relative)) }
# Public documentation is named explicitly. Do not copy directories, local logs, private screenshots or historical artifacts.
foreach ($relative in @(
    'README.md', 'README.en.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'TESTING.md', 'CHANGELOG.md',
    "release-notes/v$Version.md", 'docs/user-guide.md', 'docs/how-it-works.md', 'docs/development.md',
    'docs/screenshots/README.md', 'docs/screenshots/portrait-placeholder.svg',
    'docs/screenshots/landscape-placeholder.svg', 'docs/screenshots/ipad-placeholder.svg',
    'docs/screenshots/dark-mode-placeholder.svg', 'src/PocketDesk/Assets/PocketDesk.svg'
)) {
    $files.Add($relative, (Join-Path $root $relative))
}

foreach ($runtime in @(
    @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Source = 'LICENSE.TXT'; Destination = 'licenses/dotnet/LICENSE.txt' },
    @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Source = 'THIRD-PARTY-NOTICES.TXT'; Destination = 'licenses/dotnet/ThirdPartyNotices.txt' },
    @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Source = 'LICENSE'; Destination = 'licenses/windowsdesktop/LICENSE.txt' }
)) {
    $prefix = "runtimepack.$($runtime.Id)/"
    $packages = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($packages.Count -ne 1) { throw "Expected exactly one self-contained runtime pack: $($runtime.Id)" }
    $runtimeVersion = $packages[0].Substring($prefix.Length)
    if ($runtimeVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid runtime package version.' }
    $packageDirectory = Join-Path $NuGetPackageRoot ($runtime.Id.ToLowerInvariant() + '/' + $runtimeVersion)
    $files.Add($runtime.Destination, (Join-Path $packageDirectory $runtime.Source))
    if ($runtime.Id -eq 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
        # WPF satellite assemblies are not all emitted into the application .deps.json.
        # Use the matching runtime pack's explicit WPF resource manifest, never a recursive DLL wildcard.
        $runtimeList = [xml](Get-Content -LiteralPath (Join-Path $packageDirectory 'data/RuntimeList.xml') -Raw)
        foreach ($resource in $runtimeList.FileList.File) {
            $profile = $resource.GetAttribute('Profile')
            if ($resource.Type -ne 'Resources' -or ($profile -and $profile -notmatch '(^|;)WPF(;|$)')) { continue }
            if ($resource.Path -notmatch '^runtimes/win-x64/lib/net10\.0/([A-Za-z][A-Za-z0-9-]*/[A-Za-z0-9_.-]+\.resources\.dll)$') {
                throw 'Unexpected WPF satellite resource path.'
            }
            $relative = $Matches[1]
            $assembly = [IO.Path]::GetFileName($relative).Replace('.resources.dll', '.dll')
            if (-not $runtimeFiles.Contains($assembly)) { continue }
            if (-not $files.ContainsKey($relative)) { $files.Add($relative, (Join-Path $PublishDir $relative)) }
        }
    }
}

# Fail before staging if any required public document or runtime license is missing.
foreach ($source in $files.Values) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required release file is missing: $source" }
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release input must not be a symbolic link.' }
}
[void][IO.Directory]::CreateDirectory($OutputDir)
$stage = Join-Path $OutputDir ('.package-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
try {
    foreach ($entry in $files.GetEnumerator()) {
        $destination = Join-Path $stage $entry.Key
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath $entry.Value -Destination $destination
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($zip.Entries.Count -ne $files.Count) { throw 'Archive entry count does not match the release allowlist.' }
        foreach ($entry in $zip.Entries) {
            if (-not $files.ContainsKey($entry.FullName.Replace('\', '/'))) { throw "Unexpected archive entry: $($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumPath, "$hash  $archiveName`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Packaged $($files.Count) allowlisted files: $archivePath"
    Write-Host "SHA256 $hash"
} finally {
    # The only recursive deletion is our newly created staging directory, verified below the exact output root.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $expectedParent = [IO.Path]::TrimEndingDirectorySeparator($OutputDir)
    if ([IO.Path]::GetDirectoryName($resolvedStage) -ne $expectedParent -or [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.package-[a-f0-9]{32}$') {
        throw 'Refusing to remove an unexpected staging directory.'
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
