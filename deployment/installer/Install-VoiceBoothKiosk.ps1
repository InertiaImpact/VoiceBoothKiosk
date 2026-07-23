[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles "VoiceBoothKiosk"),
    [string]$KioskUser = "$env:USERDOMAIN\$env:USERNAME",
    [switch]$SkipSupervisedStartup,
    [switch]$FilesOnly,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$taskName = "VoiceBoothKiosk-Supervised"
$script:installerProgressForm = $null
$script:installerProgressLabel = $null
$script:installerProgressBar = $null
$script:installerProgressCloseButton = $null

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Write-InstallerStatus([string]$Message) {
    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Initialize-InstallerProgress {
    if ($Quiet -or -not [Environment]::UserInteractive) {
        return
    }

    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing

        $form = New-Object System.Windows.Forms.Form
        $form.Text = "Voice Booth Kiosk Setup"
        $form.ClientSize = New-Object System.Drawing.Size(480, 184)
        $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
        $form.MaximizeBox = $false
        $form.MinimizeBox = $false
        $form.ControlBox = $false
        $form.ShowInTaskbar = $true
        $form.TopMost = $true
        $form.BackColor = [System.Drawing.Color]::FromArgb(245, 247, 250)

        $heading = New-Object System.Windows.Forms.Label
        $heading.Text = "Installing Voice Booth Kiosk"
        $heading.AutoSize = $false
        $heading.Location = New-Object System.Drawing.Point(24, 20)
        $heading.Size = New-Object System.Drawing.Size(432, 28)
        $heading.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
        $heading.ForeColor = [System.Drawing.Color]::FromArgb(24, 35, 50)

        $status = New-Object System.Windows.Forms.Label
        $status.Text = "Preparing installation..."
        $status.AutoSize = $false
        $status.Location = New-Object System.Drawing.Point(24, 56)
        $status.Size = New-Object System.Drawing.Size(432, 38)
        $status.Font = New-Object System.Drawing.Font("Segoe UI", 9)
        $status.ForeColor = [System.Drawing.Color]::FromArgb(71, 84, 103)

        $progress = New-Object System.Windows.Forms.ProgressBar
        $progress.Location = New-Object System.Drawing.Point(24, 102)
        $progress.Size = New-Object System.Drawing.Size(432, 18)
        $progress.Minimum = 0
        $progress.Maximum = 100
        $progress.Value = 2
        $progress.Style = [System.Windows.Forms.ProgressBarStyle]::Continuous

        $closeButton = New-Object System.Windows.Forms.Button
        $closeButton.Text = "Close"
        $closeButton.Location = New-Object System.Drawing.Point(356, 136)
        $closeButton.Size = New-Object System.Drawing.Size(100, 32)
        $closeButton.Font = New-Object System.Drawing.Font("Segoe UI", 9)
        $closeButton.Visible = $false
        $closeButton.Add_Click({
            if ($null -ne $script:installerProgressForm) {
                $script:installerProgressForm.Close()
            }
        })

        $form.Controls.AddRange(@($heading, $status, $progress, $closeButton))
        $script:installerProgressForm = $form
        $script:installerProgressLabel = $status
        $script:installerProgressBar = $progress
        $script:installerProgressCloseButton = $closeButton
        $form.Show()
        $form.Activate()
        [System.Windows.Forms.Application]::DoEvents()
    }
    catch {
        Write-Warning "The visual installer progress window could not be opened. Console status will still be shown. $($_.Exception.Message)"
    }
}

function Set-InstallerProgress([string]$Message, [int]$Percent) {
    Write-InstallerStatus $Message
    if ($null -eq $script:installerProgressForm -or $script:installerProgressForm.IsDisposed) {
        return
    }

    $script:installerProgressLabel.Text = $Message
    $script:installerProgressBar.Value = [Math]::Max(
        $script:installerProgressBar.Minimum,
        [Math]::Min($script:installerProgressBar.Maximum, $Percent))
    $script:installerProgressForm.Refresh()
    [System.Windows.Forms.Application]::DoEvents()
}

function Complete-InstallerProgress([string]$Message) {
    Set-InstallerProgress $Message 100
    if ($null -eq $script:installerProgressForm -or $script:installerProgressForm.IsDisposed) {
        return
    }

    $script:installerProgressCloseButton.Visible = $true
    $script:installerProgressCloseButton.Focus()
    $script:installerProgressForm.Activate()
    while ($script:installerProgressForm.Visible) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 50
    }
    $script:installerProgressForm.Dispose()
}

function Suspend-SupervisedStartupForUpgrade {
    if ($FilesOnly) {
        return $null
    }

    Import-Module ScheduledTasks
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -eq $existingTask) {
        return $null
    }

    $state = [pscustomobject]@{
        WasEnabled = [bool]$existingTask.Settings.Enabled
        WasRunning = [string]$existingTask.State -eq "Running"
    }
    if ($state.WasEnabled) {
        Disable-ScheduledTask -TaskName $taskName | Out-Null
    }
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    return $state
}

function Restore-SupervisedStartupAfterFailedUpgrade($PreviousState) {
    if ($null -eq $PreviousState -or -not $PreviousState.WasEnabled) {
        return
    }

    try {
        Enable-ScheduledTask -TaskName $taskName | Out-Null
        if ($PreviousState.WasRunning) {
            Start-ScheduledTask -TaskName $taskName
        }
    }
    catch {
        Write-Warning "The upgrade failed and the previous supervised startup task could not be restored automatically: $($_.Exception.Message)"
    }
}

function Stop-VoiceBoothApplications {
    $processNames = @("VoiceBoothKiosk", "VoiceBoothKiosk.Config")
    $running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Set-InstallerProgress "Closing running Voice Booth applications before upgrading..." 34
    foreach ($process in $running) {
        try {
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                $null = $process.CloseMainWindow()
            }
        }
        catch {
        }
    }

    $graceDeadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 250
        $running = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    } while ($running.Count -gt 0 -and [DateTime]::UtcNow -lt $graceDeadline)

    if ($running.Count -gt 0) {
        Set-InstallerProgress "Stopping remaining Voice Booth background processes..." 38
        foreach ($process in $running) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        $remaining = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    } while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $stopDeadline)

    if ($remaining.Count -gt 0) {
        $identifiers = ($remaining | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ", "
        throw "Voice Booth could not be stopped for the upgrade: $identifiers. Restart Windows and run the installer again."
    }
}

if (-not $FilesOnly -and -not (Test-IsAdministrator)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", (Quote-ProcessArgument $PSCommandPath),
        "-InstallRoot", (Quote-ProcessArgument $InstallRoot),
        "-KioskUser", (Quote-ProcessArgument $KioskUser)
    )
    if ($SkipSupervisedStartup) { $arguments += "-SkipSupervisedStartup" }
    if ($Quiet) { $arguments += "-Quiet" }

    $elevated = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
}

$payloadPath = Join-Path $PSScriptRoot "VoiceBoothKiosk.Payload.zip"
$uninstallerSource = Join-Path $PSScriptRoot "Uninstall-VoiceBoothKiosk.ps1"
$installGuideSource = Join-Path $PSScriptRoot "INSTALL.md"
if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
    throw "The installer payload is missing: $payloadPath"
}
if (-not (Test-Path -LiteralPath $uninstallerSource -PathType Leaf)) {
    throw "The uninstaller is missing: $uninstallerSource"
}

$fullInstallRoot = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$installDriveRoot = [IO.Path]::GetPathRoot($fullInstallRoot)
if ($fullInstallRoot.TrimEnd('\') -eq $installDriveRoot.TrimEnd('\')) {
    throw "Refusing to install into a drive root."
}

if (-not $FilesOnly) {
    try {
        $account = [Security.Principal.NTAccount]::new($KioskUser)
        $null = $account.Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "The kiosk account '$KioskUser' could not be resolved. Create it and sign in once before installing."
    }
}

Initialize-InstallerProgress
Set-InstallerProgress "Preparing and validating the installation..." 5

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("VoiceBoothKiosk-Install-" + [Guid]::NewGuid().ToString("N"))
$version = "unknown"
$suspendedTaskState = $null
$installationCompleted = $false
$removeSupervisedTaskAfterInstall = $false
$completionMessage = "Voice Booth Kiosk installation complete."
try {
    Set-InstallerProgress "Extracting and validating Voice Booth..." 15
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    Expand-Archive -LiteralPath $payloadPath -DestinationPath $temporaryRoot -Force

    $sourceRecorder = Join-Path $temporaryRoot "recorder"
    $sourceConfig = Join-Path $temporaryRoot "config"
    foreach ($requiredFile in @(
        (Join-Path $sourceRecorder "VoiceBoothKiosk.exe"),
        (Join-Path $sourceRecorder "VoiceBoothKiosk.Supervision.dll"),
        (Join-Path $sourceConfig "VoiceBoothKiosk.Config.exe")
    )) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "The installer payload is incomplete: $requiredFile"
        }
    }

    if ($PSCmdlet.ShouldProcess($fullInstallRoot, "Install Voice Booth Kiosk")) {
        Set-InstallerProgress "Stopping supervised startup for the upgrade..." 30
        $suspendedTaskState = Suspend-SupervisedStartupForUpgrade
        Stop-VoiceBoothApplications

        Set-InstallerProgress "Replacing application files..." 45
        New-Item -ItemType Directory -Path $fullInstallRoot -Force | Out-Null
        foreach ($folderName in @("recorder", "config")) {
            $destination = [IO.Path]::GetFullPath((Join-Path $fullInstallRoot $folderName))
            $expectedPrefix = $fullInstallRoot.TrimEnd('\') + '\'
            if (-not $destination.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to replace an install folder outside the selected root."
            }
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Recurse -Force
            }
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item -Path (Join-Path $temporaryRoot "$folderName\*") -Destination $destination -Recurse -Force
        }

        Set-InstallerProgress "Writing installation information..." 65
        Copy-Item -LiteralPath $uninstallerSource -Destination (Join-Path $fullInstallRoot "Uninstall-VoiceBoothKiosk.ps1") -Force
        if (Test-Path -LiteralPath $installGuideSource -PathType Leaf) {
            Copy-Item -LiteralPath $installGuideSource -Destination (Join-Path $fullInstallRoot "INSTALL.md") -Force
        }

        $recorderPath = Join-Path $fullInstallRoot "recorder\VoiceBoothKiosk.exe"
        $configPath = Join-Path $fullInstallRoot "config\VoiceBoothKiosk.Config.exe"
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($recorderPath).ProductVersion
        [ordered]@{
            product = "Voice Booth Kiosk"
            version = $version
            installedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            kioskUser = $KioskUser
            supervisedStartup = -not $SkipSupervisedStartup -and -not $FilesOnly
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fullInstallRoot "install.json") -Encoding UTF8

        if (-not $FilesOnly) {
            Set-InstallerProgress "Configuring data permissions..." 74
            $machineDataRoot = Join-Path $env:ProgramData "VoiceBoothKiosk"
            $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                [Security.AccessControl.InheritanceFlags]::ObjectInherit
            foreach ($directory in @(
                (Join-Path $machineDataRoot "config"),
                (Join-Path $machineDataRoot "logs")
            )) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
                $acl = Get-Acl -LiteralPath $directory
                $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                    $KioskUser,
                    [Security.AccessControl.FileSystemRights]::Modify,
                    $inheritance,
                    [Security.AccessControl.PropagationFlags]::None,
                    [Security.AccessControl.AccessControlType]::Allow)
                $acl.SetAccessRule($rule)
                Set-Acl -LiteralPath $directory -AclObject $acl
            }

            Set-InstallerProgress "Configuring supervised full-screen startup..." 84
            Import-Module ScheduledTasks
            if ($SkipSupervisedStartup) {
                $removeSupervisedTaskAfterInstall = $true
            }
            else {
                $action = New-ScheduledTaskAction `
                    -Execute $recorderPath `
                    -Argument "--supervised-kiosk" `
                    -WorkingDirectory (Split-Path -Parent $recorderPath)
                $trigger = New-ScheduledTaskTrigger -AtLogOn -User $KioskUser
                $principal = New-ScheduledTaskPrincipal -UserId $KioskUser -LogonType Interactive -RunLevel Limited
                $settings = New-ScheduledTaskSettingsSet `
                    -AllowStartIfOnBatteries `
                    -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable `
                    -ExecutionTimeLimit ([TimeSpan]::Zero) `
                    -MultipleInstances IgnoreNew `
                    -RestartCount 3 `
                    -RestartInterval (New-TimeSpan -Minutes 2)
                $task = New-ScheduledTask `
                    -Action $action `
                    -Trigger $trigger `
                    -Principal $principal `
                    -Settings $settings `
                    -Description "Starts Voice Booth in supervised full-screen mode for the kiosk account."
                Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
            }

            Set-InstallerProgress "Creating application shortcuts..." 92
            $programsRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
            $shortcutRoot = Join-Path $programsRoot "Voice Booth Kiosk"
            New-Item -ItemType Directory -Path $shortcutRoot -Force | Out-Null
            $shell = New-Object -ComObject WScript.Shell
            try {
                $recorderShortcut = $shell.CreateShortcut((Join-Path $shortcutRoot "Voice Booth Kiosk.lnk"))
                $recorderShortcut.TargetPath = $recorderPath
                $recorderShortcut.Arguments = "--supervised-kiosk"
                $recorderShortcut.WorkingDirectory = Split-Path -Parent $recorderPath
                $recorderShortcut.IconLocation = "$recorderPath,0"
                $recorderShortcut.Description = "Start Voice Booth in supervised full-screen mode"
                $recorderShortcut.Save()

                $settingsShortcut = $shell.CreateShortcut((Join-Path $shortcutRoot "Voice Booth Settings.lnk"))
                $settingsShortcut.TargetPath = $configPath
                $settingsShortcut.WorkingDirectory = Split-Path -Parent $configPath
                $settingsShortcut.IconLocation = "$configPath,0"
                $settingsShortcut.Description = "Configure Voice Booth"
                $settingsShortcut.Save()

                $uninstallShortcut = $shell.CreateShortcut((Join-Path $shortcutRoot "Uninstall Voice Booth Kiosk.lnk"))
                $uninstallShortcut.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
                $uninstallShortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$fullInstallRoot\Uninstall-VoiceBoothKiosk.ps1`""
                $uninstallShortcut.WorkingDirectory = $fullInstallRoot
                $uninstallShortcut.Description = "Uninstall Voice Booth Kiosk"
                $uninstallShortcut.Save()
            }
            finally {
                if ($null -ne $shell) {
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
                }
            }

            if ($removeSupervisedTaskAfterInstall) {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            }
            elseif ($null -ne $suspendedTaskState -and $suspendedTaskState.WasRunning) {
                Set-InstallerProgress "Restarting Voice Booth in supervised full-screen mode..." 97
                Start-ScheduledTask -TaskName $taskName
            }
        }
    }

    $installationCompleted = $true
    Write-InstallerStatus "Voice Booth Kiosk $version installed at $fullInstallRoot."
    if (-not $FilesOnly -and -not $SkipSupervisedStartup -and
        ($null -eq $suspendedTaskState -or -not $suspendedTaskState.WasRunning)) {
        Write-InstallerStatus "Supervised full-screen mode will start the next time $KioskUser signs in."
    }
    Write-InstallerStatus "Automatic Windows sign-in must be configured separately according to your organization's policy."
    $completionMessage = "Installation complete. Voice Booth Kiosk $version is ready."
}
finally {
    if (-not $installationCompleted) {
        Restore-SupervisedStartupAfterFailedUpgrade $suspendedTaskState
    }

    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith("VoiceBoothKiosk-Install-", [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Complete-InstallerProgress $completionMessage
