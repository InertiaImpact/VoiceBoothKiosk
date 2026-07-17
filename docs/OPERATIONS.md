# Voice Booth Kiosk Operations and Recovery

## Normal operator flow

1. Use the top-right **Audio Devices** icon for the microphone/headphone check. Press and hold the adjacent **Settings** gear for four seconds if the save location or another kiosk option needs to change; releasing early cancels it.
2. Enter a session name in the suggested `Name-topic` format.
3. Select Record, then use the same button to pause recording before playback or another action.
4. Resume Recording to append to that clip, or select New Clip for a separate MP3/WAV pair. During review, drag across the waveform to measure a time range.
5. Rename clips where a descriptive filename is useful.
6. Select End Session, review the output filenames, choose an enabled destination, and save.
7. If the session has no recorded audio, select **End Empty Session** instead. The app removes the temporary local session and returns to Welcome without creating a destination folder.
8. For a recorded session, wait for the Session Saved screen before removing removable media or disconnecting a network destination.

## Startup self-checks

Before public use, verify:

- The intended input and output endpoints appear under Audio Devices.
- Start Mic Test, speak for at least 10 seconds, and confirm the live meter continues moving until Stop Mic Test is selected. Closing the panel or changing microphones must also release it.
- Test Speakers plays an audible two-note tone through the selected headphones/speakers and reports success.
- Local free-space status is healthy.
- The startup check reports the configured Media Foundation MP3 profile as ready (128 kbps by default).
- At least one approved export destination is configured and writable.
- A short test session produces a playable MP3 and a matching readable WAV in the session's `Raw` folder.

## Inactivity privacy protection

For an active session that is Ready, Playing, or Paused, the configured inactivity interval starts a visible 60-second warning. Any user interaction cancels the warning and keeps the session open. If the countdown expires, the app secures the session by moving it to quarantine and returns to a clean welcome screen.

This is a privacy boundary between walk-up users, not a deletion policy. An administrator can preserve or recover the quarantined work under `%LocalAppData%\VoiceBoothKiosk\quarantine` before retention cleanup. Set the inactivity interval to `0` only when site policy intentionally disables this behavior.

## Interrupted session recovery

Stopped segments and the session manifest are saved locally. If the app or computer stops before End Session completes, the next launch reports an interrupted session.

- Resume restores the session and its finalized clips.
- Starting over moves the session to quarantine; it does not immediately erase the only audio copy.
- `.wav.partial` files represent a recording interrupted before its final Stop. On discovery, the app validates the expected 48 kHz, 16-bit mono PCM structure, repairs stale RIFF/data lengths when a non-empty block-aligned payload is safe, promotes the file to `.wav`, and updates segment metadata. Invalid or empty partials are not accepted as recovered audio.
- A power loss can still lose samples buffered by the audio driver or process but not yet written to the partial file.

Quarantined data is under `%LocalAppData%\VoiceBoothKiosk\quarantine` for the kiosk account. Apply the configured retention policy only after confirming whether the material is still needed.

## Export failure

If End Session fails:

1. Leave the application open; local working audio is retained.
2. Verify the destination is connected, writable, and has enough free space.
3. Select another approved destination if available.
4. Retry End Session.
5. If retries fail, collect the redacted logs and preserve the local work directory before servicing.

Final output is created transactionally. Every non-empty clip produces a delivery MP3 in the session folder and a lossless 48 kHz, 16-bit, mono PCM WAV under `Raw`. A failed export may leave a hidden `.voicebooth-*` staging directory or reservation marker at the target, but it does not expose an incomplete final folder or overwrite an existing one. Remove stale staging only after verifying the local session is safe.

A successfully committed folder contains hidden `.voicebooth-export.json` metadata. If the machine stops after the destination folder is committed but before the local manifest records completion, startup validates this receipt, its exact finalized-segment set, every listed MP3, and every listed Raw WAV. A match completes the existing export instead of producing a duplicate folder. Leave the hidden receipt in place; it contains no samples or user-entered display text.

## Device failure during recording

If a microphone is unplugged or invalidated:

- The app stops the active capture operation and preserves every previously stopped segment.
- Reconnect the device, open Audio Devices, select it again, and run Start Mic Test.
- Continue recording after the app returns to Ready.
- Do not assume the interrupted segment contains a complete take; play it back after recovery.

## Configuration editor

Press and hold the small top-right **Settings** gear for four seconds on the welcome screen or while a session is stopped. A progress line under the icon shows the hold and releasing, moving away, or losing focus early cancels it. A completed hold opens `VoiceBoothKiosk.Config.exe` without a password or UAC elevation prompt; the editor is intentionally ungated. It can also be launched directly while servicing the workstation.

Configuration changes are validated before atomic save. The previous known-good JSON remains available as a backup if a write is interrupted. The Branding tab includes editable hex values plus a lightweight visual picker with presets and RGB sliders. Selecting a logo immediately copies it into the machine configuration's managed `assets` folder; the original USB, share, or source file can then be removed without breaking the logo. The setting is not activated until **Save and apply** is selected. When the editor closes, the recorder reloads saved settings. During a stopped active session, destination, appearance, and live-policy changes can apply immediately; its audio/device and clip-name snapshot remains unchanged until the next session.

To copy a setup between stations:

1. Open Settings on the configured station and choose **Export preset...**.
2. Save the `.voicebooth.zip` package to approved removable media or a shared deployment location. The package contains the complete validated configuration and embeds the current supported logo file when one is configured.
3. On another station, open Settings and choose **Import preset...**.
4. Review audio-device IDs and destination paths, because those values are included and may be station-specific.
5. Choose **Save and apply** to activate the imported preset. Cancel or close the editor to leave the active station configuration unchanged.

Preset import reads only the known manifest and optional logo entry; it does not extract arbitrary ZIP contents. The embedded logo is size-limited, SHA-256 verified, and installed under the machine configuration's managed `assets` folder. Supported embedded logo types are PNG, JPEG, BMP, and GIF.

To configure where completed sessions are saved:

1. Open the **Destinations** tab and select an existing row, or choose **Add**.
2. Enter a label and choose **Browse path…** to select an absolute local, removable, or UNC folder.
3. Enable the row, choose **Check selected**, and resolve any write-permission or free-space error.
4. Choose **Set default** if this should be the preselected End Session destination.
5. Choose **Save and apply**. Other enabled destinations remain selectable when ending a session.

Important destination rules:

- Use absolute local, removable, or UNC roots.
- Grant the kiosk account create/write permission only where required.
- Do not grant broad write access to the application install directory.
- Test UNC authentication under the actual kiosk account.
- Keep the minimum-free-space threshold large enough for the longest expected session.

## Logs and support bundle

Logs are JSON Lines in `%ProgramData%\VoiceBoothKiosk\logs`, with a LocalAppData fallback if the kiosk account cannot write the machine log directory. Logs contain state/errors/device metadata, not audio samples. Session names should not be added to logs during future development without a privacy review.

When escalating a problem, collect:

- Application version and Windows edition/build
- Sanitized `machine-settings.json` and `user-settings.json`
- Recent log files
- Input/output endpoint names and interface driver version
- Free-space and destination type
- Whether the issue followed a device disconnect, reboot, or interrupted export

Do not include recordings unless an authorized person explicitly approves the chosen files.

## Servicing and update safety

1. Confirm no session is active.
2. Exit through the configured Windows kiosk breakout and sign in as an administrator.
3. Back up quarantined/work data and configuration.
4. Install the signed release.
5. Launch in `--windowed` mode as an operator for a short hardware/export test.
6. Re-enable the Shell Launcher kiosk account and reboot.
7. Verify automatic sign-in, app restart behavior, device tests, and a test export.

Never replace binaries while a recording or export is active.

## Supervised auto-login deployment

For a station that only needs automatic sign-in and a full-screen recorder over the normal Windows desktop, launch `VoiceBoothKiosk.exe --supervised-kiosk` from the kiosk account's Startup folder. The switch forces borderless full-screen and always-on-top behavior even if those two visual settings were disabled previously. It also enables a faint top-right X that requires a continuous four-second hold. A normal click, early release, pointer departure, or focus loss does not exit, and Alt+F4 remains blocked.

Run `deployment\Install-SupervisedKioskStartup.ps1` while signed in as the intended account to create the shortcut. This is a supervised presentation mode, not hardened kiosk security; Windows shortcuts and other OS entry points remain available unless Windows policies restrict them.

## Build and publish verification

From the repository root on Windows 11 x64 with the .NET 10 SDK:

```powershell
dotnet restore VoiceBoothKiosk.sln
dotnet build VoiceBoothKiosk.sln --configuration Release --no-restore
dotnet test tests\VoiceBoothKiosk.Tests\VoiceBoothKiosk.Tests.csproj --configuration Release --no-build --no-restore
.\deployment\Publish.ps1
```

`Publish.ps1` performs its own restore and, unless `-SkipTests` is supplied, a Release test run. It then publishes self-contained, untrimmed `win-x64` folders to `artifacts\publish\recorder` and `artifacts\publish\config`. Run a hardware capture/playback/export smoke test against those published files before signing and deployment.
