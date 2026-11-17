param(
    [switch]$Test,
    [switch]$PureTest,
    [switch]$Publish,
    [string]$PublishDir,
    [string]$BuildDir
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ($Test -and $PureTest) { throw 'Choose -Test (interactive fixture suite) or -PureTest (CI-safe policy/layout tests).' }
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$project = Join-Path $root 'src\PocketDesk\PocketDesk.csproj'
$buildArguments = @()
if ($BuildDir) {
    $BuildDir = [IO.Path]::GetFullPath($BuildDir, $root)
    $buildArguments += @('--artifacts-path', $BuildDir)
}
& $dotnet build $project -c Release --nologo @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Test) {
    & $dotnet run --project (Join-Path $root 'tests\PocketDesk.Tests\PocketDesk.Tests.csproj') -c Release @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($PureTest) {
    $testProject = Join-Path $root 'tests\PocketDesk.Tests\PocketDesk.Tests.csproj'
    # These explicit entry points do not create the native window fixture or change display modes.
    & $dotnet run --project $testProject -c Release @buildArguments -- --geometry-tests
    if ($LASTEXITCODE -ne 0) { throw 'Pure screen geometry/recovery tests failed.' }
    & $dotnet run --project $testProject -c Release --no-build --no-restore @buildArguments -- --layout-tests
    if ($LASTEXITCODE -ne 0) { throw 'Pure layout tests failed.' }
}
if ($Publish) {
    if (-not $PublishDir) { $PublishDir = Join-Path $root 'artifacts\PocketDesk-win-x64' }
    $PublishDir = [IO.Path]::GetFullPath($PublishDir, $root)
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $PublishDir --nologo @buildArguments '-p:DebugType=None' '-p:DebugSymbols=false' "-p:PathMap=$root=/_/"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Write-Host "Published to $PublishDir. Use scripts/package-release.ps1 to produce the public ZIP."
}
