[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles "VoiceBoothKiosk"),
    [switch]$PurgeMachineData,
    [switch]$FilesOnly,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$taskName = "VoiceBoothKiosk-Supervised"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

if (-not $FilesOnly -and -not (Test-IsAdministrator)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", (Quote-ProcessArgument $PSCommandPath),
        "-InstallRoot", (Quote-ProcessArgument $InstallRoot)
    )
    if ($PurgeMachineData) { $arguments += "-PurgeMachineData" }
    if ($Quiet) { $arguments += "-Quiet" }

    $elevated = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
}

$fullInstallRoot = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$installDriveRoot = [IO.Path]::GetPathRoot($fullInstallRoot)
if ($fullInstallRoot.TrimEnd('\') -eq $installDriveRoot.TrimEnd('\') -or
    [IO.Path]::GetFileName($fullInstallRoot) -ne "VoiceBoothKiosk") {
    throw "Refusing to remove an unexpected install path: $fullInstallRoot"
}

$runningInstalledProcesses = @(Get-Process -Name "VoiceBoothKiosk", "VoiceBoothKiosk.Config" -ErrorAction SilentlyContinue | Where-Object {
    try {
        $_.Path.StartsWith($fullInstallRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        $false
    }
})
if ($runningInstalledProcesses.Count -gt 0) {
    throw "Voice Booth or its Settings editor is running. End any active session, close Settings, use the four-second X, and run the uninstaller again."
}

if ($PSCmdlet.ShouldProcess($fullInstallRoot, "Uninstall Voice Booth Kiosk")) {
    if (-not $FilesOnly) {
        Import-Module ScheduledTasks
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

        $programsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
        $shortcutRoot = [IO.Path]::GetFullPath((Join-Path $programsRoot "Voice Booth Kiosk"))
        $expectedProgramsPrefix = [IO.Path]::GetFullPath($programsRoot).TrimEnd('\') + '\'
        if ($shortcutRoot.StartsWith($expectedProgramsPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $shortcutRoot)) {
            Remove-Item -LiteralPath $shortcutRoot -Recurse -Force
        }
    }

    if (Test-Path -LiteralPath $fullInstallRoot) {
        Remove-Item -LiteralPath $fullInstallRoot -Recurse -Force
    }

    if ($PurgeMachineData -and -not $FilesOnly) {
        $machineDataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData "VoiceBoothKiosk"))
        $programDataPrefix = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'
        if (-not $machineDataRoot.StartsWith($programDataPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($machineDataRoot) -ne "VoiceBoothKiosk") {
            throw "Refusing to purge an unexpected machine-data path."
        }
        if (Test-Path -LiteralPath $machineDataRoot) {
            Remove-Item -LiteralPath $machineDataRoot -Recurse -Force
        }
    }
}

if (-not $Quiet) {
    Write-Host "Voice Booth Kiosk was uninstalled."
    if (-not $PurgeMachineData -and -not $FilesOnly) {
        Write-Host "Machine configuration and logs were preserved under $env:ProgramData\VoiceBoothKiosk."
    }
    Write-Host "Per-user recordings and recovery data were not deleted."
}
