# Voice Booth Kiosk

Voice Booth Kiosk is a Windows 11 walk-up audio recorder designed for a full-screen workstation. A user names a session, records and reviews one or more clips, and ends the session to export verified MP3 files plus lossless archival WAV files to a configured destination.

## What is implemented

- Full-screen, touch-friendly WPF recorder UI
- Session-name prompt with Windows-safe folder preview
- Record/pause/resume recording, playback pause-to-edit, seek, and return-to-start
- Audacity-inspired live waveform, timeline, playhead, drag-to-measure time selections, and input meter
- Multiple clips with selection, custom naming, rename pencil, deletion, undo-capable storage, New Clip, and New Take
- Persistent input/output device selection, continuous microphone and speaker tests, playback-volume control, and driver-supported Windows microphone gain control
- Local lossless WAV segments, atomic manifests, and automatic repair of structurally valid interrupted WAVs
- Guided End Session flow with destination selection and filename preview; an empty walk-away session can be ended without creating an export folder
- Optional streamlined End Session behavior that skips the picker, saves to the configured default destination, and shows the normal saved-files confirmation
- Transactional 128 kbps Media Foundation MP3 export plus matching lossless WAV archival, with exact-profile preflight, collision protection, and output validation
- A durable hidden export receipt that prevents a restart after destination commit from creating a duplicate session folder
- Small top-right Audio Devices and Settings icons, with an ungated editor for instructions, title/subtitle branding, actual-scale identity preview, aspect-aware PNG/JPG/BMP/GIF/SVG logos, in-line and pop-up visual color selection, two stacked welcome supporting blocks, destinations, policies, accessibility, retention, audio defaults, live recorder-readiness details, log access, and launch-mode-aware desktop shortcut creation; instruction text accepts Enter or literal `\n` line breaks
- Portable ZIP preset import/export for copying the complete validated configuration, colors, content, and an embedded logo between stations
- Versioned JSON settings, rolling logs, recovery quarantine, free-space checks, inactivity privacy timeout, and single-instance enforcement
- Windows Shell Launcher deployment guidance

## Technology

- C# 14 / .NET 10 LTS
- Windows Presentation Foundation (WPF)
- NAudio 2.3 over Windows WASAPI
- Windows Media Foundation MP3 encoder
- xUnit tests

## Quick start for development

Requirements: Windows 11 x64 and the .NET 10 SDK.

```powershell
dotnet restore VoiceBoothKiosk.sln
dotnet build VoiceBoothKiosk.sln
dotnet test tests\VoiceBoothKiosk.Tests\VoiceBoothKiosk.Tests.csproj
dotnet run --project src\VoiceBoothKiosk.App\VoiceBoothKiosk.App.csproj -- --windowed
```

`--windowed` makes the recorder resizable and closable for development. Production launches without that switch.

For a simpler auto-login deployment that retains the normal Windows desktop underneath the recorder, launch with `--supervised-kiosk`. This starts a hidden helper plus the visible recorder. The helper checks every two minutes, restoring and foregrounding the full-screen recorder when it is running and relaunching it two minutes after an unexpected exit. Completing the faint four-second hold-to-exit X sends an intentional exit signal and closes both processes without a relaunch. Alt+F4 remains blocked. This is operational convenience, not Windows lockdown.

Press and hold the small **Settings** gear in the top-right of the welcome screen or stopped-session toolbar for four seconds to open the configuration editor. Releasing or moving away early cancels the hold. The editor intentionally has no password prompt and runs without elevation. You can also launch it directly during development:

```powershell
dotnet run --project src\VoiceBoothKiosk.Config\VoiceBoothKiosk.Config.csproj
```

For non-administrator development, keep all runtime data inside the repo or another writable test directory:

```powershell
$env:VOICEBOOTH_MACHINE_DATA_ROOT = "$PWD\.devdata\machine"
$env:VOICEBOOTH_LOCAL_DATA_ROOT = "$PWD\.devdata\local"
dotnet run --project src\VoiceBoothKiosk.App\VoiceBoothKiosk.App.csproj -- --windowed
```

Do not point those variables at production data. They are intended for development and automated testing.

## Applications

| Application | Purpose |
| --- | --- |
| `VoiceBoothKiosk` | Public full-screen recorder; runs as a standard kiosk user |
| `VoiceBoothKiosk.Config` | Ungated settings editor launched by the recorder; runs as the current user without UAC elevation |

The editor's **Destinations** tab can add save roots, browse to a folder, test that the selected root is writable, enable or remove entries, and choose the default. Choosing a logo copies it into the station's managed `config\assets` folder, so the branding does not depend on the original file remaining connected; selected SVG artwork is checked for active/external content and converted once to a static PNG. The bottom-row **Import preset...** and **Export preset...** controls transfer the complete configuration and embed that logo in a portable `.voicebooth.zip` package. Import fills the editor but does not activate the preset until **Save and apply** is selected, leaving an opportunity to review station-specific devices and destinations. Closing the editor reloads the recorder automatically. For a stopped active session, new destinations apply immediately while its recording format, device snapshot, and clip-name format remain frozen. The recorder otherwise persists permitted input/output choices in the kiosk user's LocalAppData.

The **Sessions & recovery** tab shows the recorder's most recent microphone, local-storage, and MP3-encoder readiness snapshot when Settings is opened from the recorder. It can open the log folder, copy a diagnostic summary, or create a current-user Desktop shortcut that preserves the active public launch mode (`--windowed`, `--supervised-kiosk`, or standard).

## Runtime data

Default locations:

```text
%ProgramData%\VoiceBoothKiosk\
  config\machine-settings.json
  config\assets\...
  logs\

%LocalAppData%\VoiceBoothKiosk\
  state\user-settings.json
  work\<session-guid>\session.json
  work\<session-guid>\segments\...
  work\<session-guid>\peaks\...
  quarantine\...
```

An active recording never writes directly to a removable drive or network share. End Session encodes MP3s and creates matching lossless WAVs from local recovery files in a staging destination. It exposes the final session folder only after both forms of every clip have been validated.

If a session was started but no audio remains, End Session offers **End Empty Session**. It removes the temporary local session and returns to Welcome without validating a destination or creating an output folder.

An idle active session first shows a 60-second privacy warning after the configured inactivity interval. If nobody responds, the app moves the recoverable session to the kiosk account's quarantine and returns to a clean welcome screen so the next user cannot browse the previous user's work.

## Default output names

For session `Jordan-Election` started July 16, 2026:

```text
Jordan-Election_07162026\
  Jordan-Election_01_07162026.mp3
  Jordan-Election_Intro_07162026.mp3
  Raw\
    Jordan-Election_01_07162026.wav
    Jordan-Election_Intro_07162026.wav
```

Existing folders and duplicate custom clip names receive `_02`, `_03`, and later suffixes. Existing recordings are never overwritten.

The MP3 files are the convenient delivery copies. `Raw` contains one 48 kHz, 16-bit, mono PCM WAV per clip, assembled losslessly from the original recorded segments without an MP3 round trip.

Each completed session folder also contains a hidden `.voicebooth-export.json` commit receipt. It contains identifiers and MP3/Raw output filenames, not audio. The app uses it to recognize an export that reached the destination if Windows restarted before the local completion manifest was saved.

## Publishing

Run:

```powershell
.\deployment\Publish.ps1
```

The script restores, tests, and publishes self-contained `win-x64` recorder and configuration folders under `artifacts\publish`. No installer is required: keep the `recorder` and adjacent `config` folders intact and launch `artifacts\publish\recorder\VoiceBoothKiosk.exe`. Code-sign the executables and installer before production deployment.

Use `-OutputRoot <path>` to publish elsewhere or `-SkipTests` only when tests already passed for the exact commit being packaged. The publish folders are `recorder` and `config`; install both so the recorder's Settings button can open the editor.

## Installer package

After a verified publish, build the Windows installation deliverables with:

```powershell
.\deployment\Build-Installer.ps1 -SkipPublish
```

This creates a one-file interactive Setup EXE, a ZIP bundle for scripted deployment, and SHA-256 checksums under `artifacts\installer`. By default the installer places the self-contained applications under `C:\Program Files\VoiceBoothKiosk` and registers supervised full-screen mode at logon for the account that launched Setup before elevation. For in-place upgrades it temporarily disables the supervised task, stops running Voice Booth and Settings processes, replaces the binaries, and restarts supervised mode when it was previously running. It preserves ProgramData configuration and all per-user recording/recovery data during upgrades and normal uninstall. See [deployment/installer/INSTALL.md](deployment/installer/INSTALL.md).

## Kiosk deployment

The WPF window is full-screen, but Windows policy supplies the actual security boundary. See [deployment/kiosk/README.md](deployment/kiosk/README.md) for Shell Launcher, standard-account, AppLocker/WDAC, Keyboard Filter, BitLocker, servicing breakout, and validation guidance.

## Documentation

- [Product and architecture plan](docs/APPLICATION_PLAN.md)
- [Operations and recovery guide](docs/OPERATIONS.md)
- [Kiosk deployment guide](deployment/kiosk/README.md)

## Current intentional limits

- Version 1 records 48 kHz, 16-bit mono voice audio and defaults to 128 kbps MP3. A configured MP3 bitrate works only when Windows Media Foundation advertises that exact mono profile; the app refuses silent format or quality substitutions.
- Continued recording appends to the selected clip; it is not punch-in editing.
- There are no cut, effect, noise-reduction, transcription, or cloud-upload tools.
- Windows N editions require the Media Feature Pack/Media Foundation MP3 encoder.
