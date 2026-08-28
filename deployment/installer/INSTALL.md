# Voice Booth Kiosk installation

## Interactive installation

1. Sign in as the Windows account that will automatically run the kiosk.
2. End any active recording session when practical. The installer will close running Voice Booth, Settings, and supervised-watchdog processes automatically.
3. Double-click the versioned `VoiceBoothKiosk-Setup-win-x64-<version>.exe` package.
4. Approve the administrator/UAC prompt. If the kiosk account is standard, supply an authorized administrator credential.
5. Follow the visual installation progress window and click **Close** when it reports that installation is complete.
6. Sign out and back in, or restart the PC. Voice Booth starts in supervised full-screen mode.
7. Hold Settings for four seconds, configure the audio devices and destination, and complete a test recording/export.

The installer captures the account that launched Setup before elevation. It installs self-contained x64 binaries under `C:\Program Files\VoiceBoothKiosk`, grants that account Modify access only to the application configuration/log directories under ProgramData, and creates a per-account logon task. Its package version matches the recorder executable version. It does not configure Windows automatic sign-in or store account credentials.

Seeing two `VoiceBoothKiosk.exe` processes in supervised mode is expected: one is the hidden supervisor and one is the visible recorder.

## Scripted installation

Extract the versioned `VoiceBoothKiosk-InstallerBundle-win-x64-<version>.zip`, then run from an elevated PowerShell prompt:

```powershell
.\Install-VoiceBoothKiosk.ps1 -KioskUser "$env:COMPUTERNAME\VoiceBoothKiosk"
```

Use `-SkipSupervisedStartup` when deploying only the binaries. Add `-Quiet` for unattended installation without the visual progress window. The installer is upgrade-safe for configuration and recordings: it temporarily disables the supervised task, stops running application processes, replaces application binaries, and leaves `%ProgramData%\VoiceBoothKiosk` and every user's `%LocalAppData%\VoiceBoothKiosk` intact. If replacement fails, it restores the previous supervised task state. If supervised mode was running, it starts the newly installed version afterward. A forced application stop relies on the normal local recording-recovery data, so deliberately end an active session first whenever possible.

## Uninstall

Use **Start > Voice Booth Kiosk > Uninstall Voice Booth Kiosk**, or run the installed script as administrator:

```powershell
& "C:\Program Files\VoiceBoothKiosk\Uninstall-VoiceBoothKiosk.ps1"
```

Uninstall preserves machine configuration, logs, and per-user recovery data. Add `-PurgeMachineData` only when the ProgramData configuration/logs should also be removed. Per-user audio is never automatically purged.

## Signing

The generated Setup EXE and application binaries are unsigned development artifacts. Code-sign the executables, DLLs, scripts, and final installer with your organization's trusted certificate before general deployment. Unsigned packages may show Windows SmartScreen or application-control warnings.
