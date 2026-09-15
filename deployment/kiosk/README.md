# Windows Kiosk Deployment

The recorder's borderless full-screen window is only the presentation layer. Production lockdown is supplied by Windows Shell Launcher, a least-privilege account, application allowlisting, and device policies.

## Supported baseline

- Windows 11 Enterprise, Enterprise LTSC, Education, IoT Enterprise, or IoT Enterprise LTSC
- x64 hardware with a tested microphone/interface and playback endpoint
- Separate standard `VoiceBoothKiosk` account and administrator/service account
- BitLocker enabled
- Signed application binaries

Windows Pro can auto-start the full-screen app for supervised use, but it does not provide the recommended Shell Launcher/Keyboard Filter boundary.

## Simpler supervised full-screen mode

For a staffed or otherwise supervised station, Shell Launcher and Keyboard Filter are optional. Configure a standard Windows account for automatic sign-in and start the recorder with:

```text
C:\Program Files\VoiceBoothKiosk\recorder\VoiceBoothKiosk.exe --supervised-kiosk
```

This switch starts a hidden supervisor process and a visible recorder process. The recorder is forced borderless, full-screen, and always on top. Every two minutes, the supervisor restores, maximizes, and foregrounds a visible recorder window. If the recorder exits unexpectedly, the supervisor waits two minutes and relaunches it; a recoverable active session is then offered through the normal startup recovery flow.

The recorder also shows a faint X beside the top-right Settings gear. A normal click does nothing; pressing continuously for four seconds fills its progress line and sends the supervisor an intentional-exit code, closing both processes to the normal Explorer desktop without a relaunch. Releasing, moving away, or losing focus cancels the exit. Alt+F4 remains blocked. Seeing two `VoiceBoothKiosk.exe` processes in this mode is expected.

While signed in as the intended kiosk account, the included helper can create that account's Startup-folder shortcut:

```powershell
.\deployment\Install-SupervisedKioskStartup.ps1
```

Remove it from the same account with:

```powershell
.\deployment\Install-SupervisedKioskStartup.ps1 -Remove
```

The supervised mode doesn't block task switching, Windows shortcuts, Task Manager, notifications, or access to other applications. Ending the hidden supervisor process also defeats its relaunch behavior. Use the Shell Launcher procedure below when the public must not be able to reach Windows.

## Keep-awake setting

The **Kiosk & layout** tab includes **Keep the screen and system awake while Voice Booth is running**, enabled by default. Like PowerToys Awake, the recorder owns the Windows execution-state request on a dedicated long-lived thread. It reasserts the request after resume and power-status changes and once per minute as a fallback, then releases it during normal exit. The Settings diagnostics snapshot reports whether Windows accepted the request. It prevents idle display and system sleep; it does not override Windows lock-screen, screen-saver, forced-sleep, update/restart, battery-critical, MDM, or other managed power policies. Configure those policies separately for the kiosk account and device.

## Deployment sequence

1. Publish the release with `deployment\Publish.ps1` and build install artifacts with `deployment\Build-Installer.ps1 -SkipPublish`.
2. Code-sign the application files and Setup package, then regenerate distribution hashes.
3. Install the recorder and configuration editor where the kiosk account can read and execute both applications.
4. Sign in once as the kiosk account and verify the microphone, output, local work directory, and each approved export destination.
5. Select **Settings** on the welcome screen and save the kiosk configuration. The editor intentionally runs as the current user and has no password or elevation prompt.
6. Configure kiosk-account permissions:
   - Read/execute on the install directory
   - Modify on `%ProgramData%\VoiceBoothKiosk\config` so the ungated editor can save atomically
   - Append/write on the chosen log directory if machine logs are required
   - Modify on its own `%LocalAppData%\VoiceBoothKiosk`
   - Create/write on approved destination roots only
7. Enable Shell Launcher and configure the recorder as that account's shell.
8. Add AppLocker or Windows Defender Application Control rules allowing only required signed binaries.
9. Configure Keyboard Filter and a documented administrator breakout sequence.
10. Configure automatic sign-in, Windows Update maintenance windows, power policy, notifications, removable-media policy, and network credentials according to station policy.
11. Reboot and complete the validation checklist below.

## Shell Launcher

`ShellLauncher.xml.template` is a Shell Launcher v2 policy template. Replace:

- `__KIOSK_ACCOUNT_NAME__`
- `__RECORDER_EXE_PATH__`
- The profile GUID if your deployment system requires a unique value

Apply the XML using your organization's supported MDM/Intune/provisioning workflow. Microsoft documents PowerShell/MDM quickstarts, but production policy should remain centrally reversible rather than being hidden inside the application installer.

Configure the default exit action to restart the shell. The recorder runs as the signed-in standard user and must never request elevation.

The small top-right Settings gear on the welcome screen launches the separate configuration editor. If AppLocker or WDAC is enabled, allow both signed executables. The Settings editor is ungated by product design; Shell Launcher and keyboard policies restrict access to Windows itself, not access to recorder settings. Its **Destinations** tab can add and browse save roots, test write access, and set the default export location.

Shell Launcher does not itself block all other Windows components. Pair it with allowlisting and account policy.

## Keyboard Filter

Enable the Windows `Client-KeyboardFilter` optional feature and suppress escape combinations chosen during security review. Common candidates include Windows-key shortcuts, Alt+Tab, Alt+Esc, Ctrl+Esc, and Ctrl+Shift+Esc. Do not rely on the application to intercept system-secure sequences.

Configure and physically label a private service breakout sequence for administrators. Test it before the kiosk goes into public use. Safe Mode bypasses Keyboard Filter and must be covered by physical/firmware security.

The faint top-right X always requires a continuous four-second hold before closing the recorder. This delay cannot be disabled in application settings. It is convenient for supervised full-screen deployments but is not a security boundary; Shell Launcher deployments should still use the operating-system breakout procedure for servicing.

## Automatic sign-in and credentials

Use an organizationally approved automatic-sign-in method. Do not store a plaintext password in this repository or application configuration. If Sysinternals Autologon is used, follow its documented security limitations and protect physical access to the device.

UNC export destinations must be tested while signed in as the actual kiosk identity. Do not embed share credentials in `machine-settings.json`.

## Production validation checklist

- Device boots directly into Voice Booth without Explorer/taskbar exposure.
- App restarts after an unexpected process exit.
- Standard escape/task-switch/system-settings paths are blocked.
- Administrator breakout still works.
- App runs unelevated.
- Input/output tests pass with the exact installed drivers.
- A stop/restart resumes the interrupted session prompt.
- A forced reboot after Stop retains the clip.
- End Session produces readable 48 kHz mono 128 kbps MP3 files.
- Removing the destination during export reports failure and retains local audio.
- Existing destination folders/files are never overwritten.
- Low-disk warnings and configured retention work.
- The next user cannot see the prior user's session after successful completion.
- Windows updates, sleep, screen timeout, and maintenance reboots follow the approved schedule.

## Rollback

Keep the prior signed package and configuration backup. To roll back:

1. Use the administrator breakout and sign in with the service account.
2. Confirm no active session exists; preserve `%LocalAppData%\VoiceBoothKiosk\work` and `quarantine`.
3. Disable Shell Launcher for the kiosk account or temporarily set the administrator shell to Explorer.
4. Install the prior package without deleting runtime data.
5. Verify config-schema compatibility, run a test session, and re-enable the kiosk shell.

Never use `git reset`, installer cleanup, or profile deletion as a recovery procedure for recording data.
