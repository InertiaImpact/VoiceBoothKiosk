[CmdletBinding()]
param(
    [string]$PublishRoot = "",
    [string]$OutputRoot = "",
    [switch]$SkipPublish,
    [switch]$KeepStage
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $repositoryRoot "artifacts\publish"
}
elseif (-not [IO.Path]::IsPathRooted($PublishRoot)) {
    $PublishRoot = Join-Path $repositoryRoot $PublishRoot
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\installer"
}
elseif (-not [IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot $OutputRoot
}

$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts")).TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Installer output must remain beneath the repository artifacts directory."
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "Publish.ps1") -OutputRoot $PublishRoot
    if ($LASTEXITCODE -ne 0) { throw "Publishing failed; installer creation was stopped." }
}

$recorderSource = Join-Path $PublishRoot "recorder"
& (Join-Path $PSScriptRoot 'Assert-PatchedRuntime.ps1') -PublishRoot $PublishRoot
$configSource = Join-Path $PublishRoot "config"
$recorderExecutable = Join-Path $recorderSource "VoiceBoothKiosk.exe"
$recorderAssembly = Join-Path $recorderSource "VoiceBoothKiosk.dll"
$configAssembly = Join-Path $configSource "VoiceBoothKiosk.Config.dll"
foreach ($requiredFile in @(
    $recorderExecutable,
    $recorderAssembly,
    (Join-Path $recorderSource "VoiceBoothKiosk.Supervision.dll"),
    (Join-Path $configSource "VoiceBoothKiosk.Config.exe"),
    $configAssembly
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Published installer input is missing: $requiredFile"
    }
}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($recorderAssembly).Version
if ($assemblyVersion.Major -lt 0 -or $assemblyVersion.Minor -lt 0 -or $assemblyVersion.Build -lt 0) {
    throw "The recorder executable does not contain a valid release version."
}
$packageVersion = "{0}.{1}.{2}" -f $assemblyVersion.Major, $assemblyVersion.Minor, $assemblyVersion.Build
$configVersion = [Reflection.AssemblyName]::GetAssemblyName($configAssembly).Version
if ($configVersion -ne $assemblyVersion) {
    throw "Published recorder ($assemblyVersion) and configuration tool ($configVersion) versions do not match."
}
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$declaredVersion = [string]$buildProperties.Project.PropertyGroup.Version
if (-not [string]::Equals($packageVersion, $declaredVersion, [StringComparison]::Ordinal)) {
    throw "Published package version $packageVersion does not match Directory.Build.props version $declaredVersion. Run a fresh publish."
}

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("VoiceBoothKiosk-Setup-" + [Guid]::NewGuid().ToString("N"))
$bundleRoot = Join-Path $stageRoot "bundle"
$payloadRoot = Join-Path $stageRoot "payload"
try {
    New-Item -ItemType Directory -Path $bundleRoot, $payloadRoot -Force | Out-Null
    Copy-Item -LiteralPath $recorderSource -Destination (Join-Path $payloadRoot "recorder") -Recurse -Force
    Copy-Item -LiteralPath $configSource -Destination (Join-Path $payloadRoot "config") -Recurse -Force

    $payloadZip = Join-Path $bundleRoot "VoiceBoothKiosk.Payload.zip"
    Compress-Archive -Path (Join-Path $payloadRoot "*") -DestinationPath $payloadZip -CompressionLevel Optimal

    foreach ($fileName in @("Install-VoiceBoothKiosk.ps1", "Uninstall-VoiceBoothKiosk.ps1", "INSTALL.md")) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer\$fileName") -Destination (Join-Path $bundleRoot $fileName) -Force
    }

    $bundleZip = Join-Path $OutputRoot "VoiceBoothKiosk-InstallerBundle-win-x64-$packageVersion.zip"
    Compress-Archive -Path (Join-Path $bundleRoot "*") -DestinationPath $bundleZip -CompressionLevel Optimal
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "installer\INSTALL.md") -Destination (Join-Path $OutputRoot "INSTALL.md") -Force

    $iexpress = Join-Path $env:SystemRoot "System32\iexpress.exe"
    if (-not (Test-Path -LiteralPath $iexpress -PathType Leaf)) {
        throw "Windows IExpress is unavailable; the ZIP installer bundle was still created at $bundleZip"
    }

    $setupPath = Join-Path $OutputRoot "VoiceBoothKiosk-Setup-win-x64-$packageVersion.exe"
    # IExpress generates an unquoted MakeCab directive internally. Building its
    # temporary EXE under the short system-temp path avoids failures when the
    # repository path contains spaces or commas; copy the completed EXE afterward.
    $setupBuildPath = Join-Path $stageRoot "VoiceBoothKiosk-Setup-win-x64.exe"
    $sourceDirectory = $bundleRoot.TrimEnd('\') + '\'
    $sedPath = Join-Path $stageRoot "VoiceBoothKiosk-Setup.sed"
    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles

[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$setupBuildPath
FriendlyName=Voice Booth Kiosk Setup $packageVersion
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-VoiceBoothKiosk.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-VoiceBoothKiosk.ps1 -Quiet
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-VoiceBoothKiosk.ps1 -Quiet
FILE0=Install-VoiceBoothKiosk.ps1
FILE1=Uninstall-VoiceBoothKiosk.ps1
FILE2=INSTALL.md
FILE3=VoiceBoothKiosk.Payload.zip

[SourceFiles]
SourceFiles0=$sourceDirectory

[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
"@
    Set-Content -LiteralPath $sedPath -Value $sed -Encoding ASCII
    & $iexpress /N /Q $sedPath
    $iexpressExitCode = $LASTEXITCODE
    # IExpress can return after its MakeCab worker has closed but just before the
    # completed EXE is renamed into place. Allow that final handoff to settle.
    # The legacy launcher starts MakeCab asynchronously. A self-contained desktop
    # payload can take several minutes even after iexpress.exe itself returns.
    $setupDeadline = [DateTime]::UtcNow.AddMinutes(10)
    $lastSetupLength = -1L
    $stableLengthChecks = 0
    do {
        $currentSetupLength = if (Test-Path -LiteralPath $setupBuildPath -PathType Leaf) {
            (Get-Item -LiteralPath $setupBuildPath).Length
        }
        else {
            0L
        }
        if ($currentSetupLength -ge 1MB -and $currentSetupLength -eq $lastSetupLength) {
            $stableLengthChecks++
        }
        else {
            $stableLengthChecks = 0
        }
        $lastSetupLength = $currentSetupLength
        if ($stableLengthChecks -ge 4) {
            break
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $setupDeadline)
    if (-not (Test-Path -LiteralPath $setupBuildPath -PathType Leaf)) {
        throw "IExpress did not create the Setup executable. The ZIP bundle is available at $bundleZip"
    }
    $setupBuildInfo = Get-Item -LiteralPath $setupBuildPath
    if ($setupBuildInfo.Length -lt 1MB) {
        throw "IExpress created an unexpectedly small Setup executable ($($setupBuildInfo.Length) bytes). The ZIP bundle is available at $bundleZip"
    }
    if ($null -ne $iexpressExitCode -and $iexpressExitCode -ne 0) {
        Write-Warning "IExpress returned exit code $iexpressExitCode after creating the complete Setup executable; the generated output will be used."
    }
    Copy-Item -LiteralPath $setupBuildPath -Destination $setupPath -Force

    $hashPath = Join-Path $OutputRoot "SHA256SUMS.txt"
    $hashLines = foreach ($file in @($setupPath, $bundleZip)) {
        $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
    }
    Set-Content -LiteralPath $hashPath -Value $hashLines -Encoding ASCII

    # Endpoint protection or other filesystem agents can remove an output shortly
    # after the initial copy. Do not report a successful release in that case.
    Start-Sleep -Seconds 8
    foreach ($file in @($setupPath, $bundleZip)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "The generated installer disappeared after creation: $file. Check endpoint protection and filesystem activity."
        }
        $verifiedHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedLine = "$verifiedHash  $([IO.Path]::GetFileName($file))"
        if ($hashLines -cnotcontains $expectedLine) {
            throw "The installer changed after creation: $file"
        }
    }

    Write-Host "Setup EXE: $setupPath"
    Write-Host "Scripted bundle: $bundleZip"
    Write-Host "Checksums: $hashPath"
    Write-Warning "These artifacts are unsigned. Code-sign and re-hash them before production distribution."
}
finally {
    $resolvedStageRoot = [IO.Path]::GetFullPath($stageRoot)
    $expectedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($KeepStage) {
        Write-Warning "Installer staging files were retained at $resolvedStageRoot"
    }
    elseif ($resolvedStageRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStageRoot).StartsWith("VoiceBoothKiosk-Setup-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedStageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
