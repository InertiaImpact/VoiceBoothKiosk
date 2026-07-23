[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RecorderPath = "C:\Program Files\VoiceBoothKiosk\recorder\VoiceBoothKiosk.exe",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory "Voice Booth Kiosk.lnk"

if ($Remove) {
    if (Test-Path -LiteralPath $shortcutPath) {
        if ($PSCmdlet.ShouldProcess($shortcutPath, "Remove supervised kiosk startup shortcut")) {
            Remove-Item -LiteralPath $shortcutPath -Force
        }
    }
    Write-Host "Startup shortcut removed for $([Environment]::UserName)."
    return
}

$fullRecorderPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($RecorderPath))
if (-not (Test-Path -LiteralPath $fullRecorderPath -PathType Leaf)) {
    throw "Recorder executable was not found: $fullRecorderPath"
}

New-Item -ItemType Directory -Path $startupDirectory -Force | Out-Null
if ($PSCmdlet.ShouldProcess($shortcutPath, "Create supervised kiosk startup shortcut")) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $fullRecorderPath
        $shortcut.Arguments = "--supervised-kiosk"
        $shortcut.WorkingDirectory = Split-Path -Parent $fullRecorderPath
        $shortcut.Description = "Start and supervise Voice Booth in full-screen kiosk mode"
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shell) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

Write-Host "Voice Booth and its two-minute supervisor will start when $([Environment]::UserName) signs in."
