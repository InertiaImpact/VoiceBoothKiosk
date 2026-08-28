[CmdletBinding()]
param(
    [string]$OutputRoot = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\publish"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot $OutputRoot
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$outputLeaf = [System.IO.Path]::GetFileName($OutputRoot.TrimEnd('\'))
$outputParent = Split-Path -Parent $OutputRoot
if ([string]::IsNullOrWhiteSpace($outputLeaf) -or [string]::IsNullOrWhiteSpace($outputParent)) {
    throw "Publish output must be a named directory, not a filesystem root."
}
$stagingRoot = Join-Path $outputParent ("$outputLeaf.staging-" + [Guid]::NewGuid().ToString("N"))
$recorderOutput = Join-Path $stagingRoot "recorder"
$configOutput = Join-Path $stagingRoot "config"

# `dotnet publish --output` normalizes its path into an MSBuild property. A
# comma in an ancestor folder (for example "Company, Inc") is then parsed as
# a property separator by MSBuild. Pass PublishDir ourselves and escape the
# MSBuild-reserved property-list characters so publishing works from any
# valid Windows checkout path.
function ConvertTo-MsBuildPropertyValue([string]$Value) {
    return $Value.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C")
}

$recorderPublishDir = ConvertTo-MsBuildPropertyValue $recorderOutput
$configPublishDir = ConvertTo-MsBuildPropertyValue $configOutput

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    dotnet restore VoiceBoothKiosk.sln --maxcpucount:1
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed." }

    # Self-contained publish needs RID-specific runtime packs in each app's
    # assets file before the --no-restore publish steps below.
    dotnet restore src\VoiceBoothKiosk.App\VoiceBoothKiosk.App.csproj --runtime win-x64 --maxcpucount:1
    if ($LASTEXITCODE -ne 0) { throw "Recorder runtime restore failed." }

    dotnet restore src\VoiceBoothKiosk.Config\VoiceBoothKiosk.Config.csproj --runtime win-x64 --maxcpucount:1
    if ($LASTEXITCODE -ne 0) { throw "Configuration-tool runtime restore failed." }

    if (-not $SkipTests) {
        dotnet test tests\VoiceBoothKiosk.Tests\VoiceBoothKiosk.Tests.csproj --configuration Release --no-restore --maxcpucount:1
        if ($LASTEXITCODE -ne 0) { throw "Tests failed; publish was stopped." }
    }

    dotnet publish src\VoiceBoothKiosk.App\VoiceBoothKiosk.App.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --maxcpucount:1 `
        -p:PublishDir=$recorderPublishDir `
        -p:PublishReadyToRun=false `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "Recorder publish failed." }

    dotnet publish src\VoiceBoothKiosk.Config\VoiceBoothKiosk.Config.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --maxcpucount:1 `
        -p:PublishDir=$configPublishDir `
        -p:PublishReadyToRun=false `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "Configuration-tool publish failed." }

    # Publish into a fresh sibling tree, then promote it only when both applications are
    # complete. This prevents stale DLLs from an older release surviving a failed publish.
    $previousRoot = "$OutputRoot.previous-" + [Guid]::NewGuid().ToString("N")
    if (Test-Path -LiteralPath $OutputRoot) {
        Move-Item -LiteralPath $OutputRoot -Destination $previousRoot
    }
    try {
        Move-Item -LiteralPath $stagingRoot -Destination $OutputRoot
    }
    catch {
        if (Test-Path -LiteralPath $previousRoot) {
            Move-Item -LiteralPath $previousRoot -Destination $OutputRoot
        }
        throw
    }
    if (Test-Path -LiteralPath $previousRoot) {
        Remove-Item -LiteralPath $previousRoot -Recurse -Force
    }

    Write-Host "Recorder: $(Join-Path $OutputRoot 'recorder')"
    Write-Host "Configuration tool: $(Join-Path $OutputRoot 'config')"
    Write-Warning "Sign and package these files before installing them on a production kiosk."
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
