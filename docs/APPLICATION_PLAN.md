# Voice Booth Kiosk: Product and Architecture Plan

Status: version 1 implementation baseline; production hardware and kiosk-policy acceptance remain  
Planning date: July 16, 2026  
Target platform: Windows 11 x64 kiosk workstation

## 1. Executive recommendation

Build the application as a Windows-native C# desktop application using .NET 10 LTS and WPF. Use NAudio as the managed audio layer over Windows WASAPI, capture to temporary lossless WAV segments, and create both final Media Foundation MP3 files and matching lossless archival WAV files when a session ends.

Deploy the app as a self-contained x64 build. On the production kiosk, run it as the custom shell under a non-administrator kiosk account using Windows Shell Launcher, with AppLocker and Keyboard Filter policies completing the lockdown. Full-screen application code alone is not a secure kiosk boundary.

The key domain decision is that a clip is a logical sequence of one or more immutable recording segments:

- Record creates a new WAV segment.
- Pause Recording closes that segment safely.
- Resume Recording appends another segment to the same logical clip.
- Playback streams all segments as one clip.
- New Take clears the selected clip's segments after confirmation.
- New Clip commits the current clip and creates the next empty clip.
- End Session joins each clip's segments, encodes one MP3 per clip, and writes the same canonical PCM to a matching WAV under `Raw`.

This design supports pause/play/resume-recording without modifying an open audio file, avoids lossy MP3-to-MP3 re-encoding, and provides straightforward crash recovery.

## 2. Product goals

The first production release should:

1. Let an untrained walk-up user start a named session quickly.
2. Make recording status unmistakable and controls touch-friendly.
3. Support multiple clips per session, with rename, select, play, continue, reset, and delete actions.
4. Show a live and recorded waveform with an elapsed-time timeline and playhead.
5. Let permitted users select input and playback devices without leaving the app.
6. Autosave all in-progress work locally and recover safely after a crash or reboot.
7. Export verified MP3 files and matching lossless Raw WAV files into a user-selectable approved destination only when End Session succeeds.
8. Return to a clean welcome screen without showing the previous user's names or audio.
9. Let a user open Settings from the welcome screen or a stopped session to customize instructions, branding, behavior, devices, destinations, and policies without a password or elevation prompt.
10. Operate without internet access after installation.

## 3. Deliberate non-goals for version 1

- Multitrack editing, cutting, effects, noise reduction, normalization, or Audacity-style destructive editing
- Punch-in recording at an arbitrary playhead position; continued recording always appends
- Cloud upload, user accounts, remote transcription, or sharing
- Simultaneous sessions or concurrent recording from multiple microphones
- Video recording
- Arbitrary user browsing of the Windows filesystem in locked patron mode

The waveform should feel familiar to an Audacity user, but version 1 is a recorder and clip manager rather than a full audio editor.

## 4. Technology decisions

| Concern | Choice | Reason |
| --- | --- | --- |
| Language/runtime | C# 14 and .NET 10 LTS | Current Windows-native LTS runtime, supported through November 2028; strong async, file, test, and desktop tooling |
| UI | WPF with XAML and MVVM | Mature Windows-only UI, flexible templates/styles, resolution-independent rendering, data binding, accessibility support, and straightforward custom waveform rendering |
| Audio I/O | NAudio 2.3 stable API over WASAPI shared mode | Enumerates Windows endpoint devices and supports capture, playback, conversion, metering, and Media Foundation encoding |
| Working audio | Canonical 48 kHz, 16-bit PCM, mono WAV segments | Lossless, recoverable, concatenates safely, and directly matches the Windows MP3 encoder's accepted PCM input |
| Final output | 48 kHz, mono, 128 kbps CBR MP3 by Windows Media Foundation plus 48 kHz, 16-bit mono PCM WAV in `Raw` | MP3 is convenient for delivery while the WAV retains the canonical recorded samples without a lossy round trip |
| App architecture | MVVM UI plus explicit domain/application/infrastructure layers | Keeps audio, storage, and export behavior testable outside the UI |
| Configuration | Versioned JSON with schema validation, atomic writes, and a separate settings editor launched from Welcome | Human-auditable, migration-friendly, and isolated from the recording process without imposing an application password or UAC prompt |
| Packaging | Self-contained `win-x64` deployment, initially folder/MSI rather than single-file | Predictable startup and native dependency behavior; no machine-wide .NET prerequisite |
| Kiosk lockdown | Shell Launcher v2, standard user, AppLocker, Keyboard Filter | Supports a WPF/Win32 desktop app while keeping Windows security outside the application |

Do not enable publish trimming initially. WPF and reflection-heavy libraries require careful trim validation, while a fixed-purpose kiosk gains little from minimizing installed size.

## 5. Primary user experience

### 5.1 Welcome and session creation

The welcome screen contains:

- Configurable logo, title, subtitle, wide welcome headline, two optional smaller stacked supporting blocks, and session-name help text
- A large Session Name field
- Suggested format text: `Name-topic`
- A live preview of the eventual folder-safe session name
- A large Start Session button
- Small, low-contrast top-right Audio Devices and Settings icons, with tooltips and accessible names

Start Session is disabled for blank or invalid names. The display name is retained exactly for the UI, while an independently sanitized name is used for files. Pressing Enter or the on-screen Start button begins the session.

### 5.2 Recording workspace

Recommended landscape layout:

| Region | Contents |
| --- | --- |
| Header | Session display name, microphone status, session elapsed time, Audio Devices, End Session |
| Left rail, about 25% | Numbered clip list with name, duration, status, selected state, rename pencil, and delete action |
| Main upper area | Selected clip name field and configurable instructions |
| Main center | Large waveform, time ruler, playhead, recorded duration, and drag-to-measure selection |
| Main lower area | Input level meter and prominent transport controls |
| Footer/status | Autosave state, storage warning, contextual help, app version in Settings/diagnostics only |

The primary button is a large red Record/Resume Recording button that becomes Pause Recording while capture is active. Recording state must also use text, iconography, elapsed-time motion, and a visible border/status treatment; color alone is insufficient.

Transport controls:

- Record / Pause Recording / Resume Recording
- Play / Pause
- Return to Start
- New Take
- New Clip
- End Session

Secondary clip actions:

- Select a clip
- Edit its name inline or with the pencil button
- Delete it with a confirmation and short-lived Undo option
- See whether it is empty, recorded, playing, or has an error

### 5.3 Interaction rules

| Current state | Allowed actions | Important rule |
| --- | --- | --- |
| Empty clip | Record, rename, select another clip, new clip, end | Play and New Take are disabled |
| Recording | Pause Recording only; emergency End first pauses safely | Device changes, playback, selection, rename, delete, and new clip are disabled |
| Recording paused | Play, resume recording, new take, new clip, rename, delete, select, end | Resumed recording appends to the end as a new safe segment |
| Playing | Pause playback or seek | Pause releases playback and returns immediately to the recording-ready state |
| Exporting | Cancel only before finalization, otherwise wait | Navigation and recording are disabled |
| Export failed | Retry, choose another approved target, return to session | Local recovery data remains intact |

Every disabled action should have a visible reason when tapped or hovered, rather than silently doing nothing.

### 5.4 New Take behavior

New Take operates on the selected clip, not the whole session. If the clip contains audio, display a confirmation that includes the clip name and duration. On confirmation:

1. Move its current segment files into a session-local recovery/trash area.
2. Clear the waveform, duration, and playhead.
3. Keep the clip name and list position.
4. Allow Undo until another recording begins or the session ends.

This makes New Take feel immediate while avoiding accidental irreversible deletion.

### 5.5 New Clip behavior

If the selected clip has audio, New Clip preserves it and selects a new empty clip named `Clip N`. If it is empty, New Clip should focus its name field rather than create chains of empty clips. Empty clips are never exported.

### 5.6 End Session flow

End Session is a guided flow, not an immediate reset:

If the active session contains no recorded audio, show an explicit **End Empty Session** confirmation, remove its temporary local work, and return to Welcome. Do not validate a destination, invoke the exporter, or create a destination folder.

1. Stop active recording or playback safely.
2. Show a summary: session name, non-empty clip count, total duration, target, and output filenames.
3. Let the user choose from destination presets configured with the editor's normal folder picker.
   When the configured skip-selection option is enabled, resolve the enabled default destination and proceed directly to export.
4. Validate destination availability, write permission, and free space.
5. Export into a hidden/staging folder with `.partial` filenames and a staged `Raw` subfolder.
6. Close, flush, and validate every MP3 and every matching lossless WAV by reopening them and checking format, length, and basic readability.
7. Flush a hidden commit receipt containing the session ID, exact finalized-segment IDs, MP3 filenames, and Raw WAV filenames.
8. Atomically move the complete staging directory to the reserved final session folder.
9. Show a clear success screen with the target label and exported clip count.
10. After a configurable short delay or a Done button, erase local working audio and reset to Welcome.

If any output fails, do not report success, do not delete the local session, and do not leave a partially completed folder appearing final. Permit retry or a different approved destination. If the final folder was committed but the local completion save was interrupted, startup validates the hidden receipt, MP3s, and Raw WAVs and adopts that existing export instead of creating a duplicate.

## 6. Session, clip, and audio state model

### 6.1 Session lifecycle

`Welcome -> Creating -> Active -> Exporting -> Completed -> Welcome`

Exceptional transitions:

- Active or Exporting -> Recoverable after process/device failure
- Exporting -> Active after a canceled or failed export
- Recoverable -> Active when resumed
- Recoverable -> Quarantined when an operator deliberately starts over
- Active -> Quarantined after the configured inactivity interval and 60-second privacy countdown

Only one active session is supported per kiosk account.

### 6.2 Domain entities

| Entity | Key fields |
| --- | --- |
| Session | Stable GUID, display name, safe name, start time, state, clips, selected clip, destination choice, config snapshot |
| Clip | Stable GUID, ordinal, display name, optional custom name flag, segment list, duration, waveform cache, state, deleted/undo metadata |
| Segment | Stable GUID, relative WAV path, start order, duration, canonical audio format, byte count, checksum/status |
| Destination | Stable ID, friendly label, root path, enabled/locked state, minimum free-space rule |
| Device selection | Endpoint ID, friendly name fallback, data flow, use-default flag |

The session stores a snapshot of the important recording/export settings so an application update or configuration change does not reinterpret an in-progress session.

## 7. Audio subsystem

### 7.1 Capture pipeline

Use Windows endpoint IDs, not list indexes, to persist devices. At record start:

1. Resolve the configured input endpoint or the current Windows default.
2. Open it in WASAPI shared mode for hardware compatibility.
3. Convert the device mix format to the canonical 48 kHz, mono, 16-bit PCM format.
4. Write samples on a background pipeline to a new segment `.wav.partial` file.
5. In parallel, calculate peak pairs for the live waveform and RMS/peak values for the input meter.
6. On Pause Recording, drain buffers, finalize the WAV header, flush, close, validate, and rename to `.wav`.
7. Append the segment metadata and atomically save the session manifest.

Audio callbacks must never block on UI rendering or slow destination/network I/O. Only the local working disk is used while recording.

Default to mono because this is a voice booth. Expose stereo as a configuration option only after testing the exact interface and microphones. Disable automatic gain control, normalization, and effects in version 1; those can make recordings unpredictable. Provide clear input-too-low and clipping indicators without altering the signal.

### 7.2 Device changes and failures

The Audio Devices panel should show:

- Input device dropdown
- Live input level meter with an explicit Start/Stop Mic Test that remains active until stopped, the device changes, or the panel closes
- Output device dropdown
- A clearly audible two-note Test Speakers action and volume slider if policy allows
- Use Windows Default toggles
- Friendly error/reconnect messages

Device selection is changeable only while idle. Save the selection immediately and use the new input on the next recording segment. If a selected endpoint disappears, stop safely, preserve all completed data, mark any incomplete segment recoverable, and offer the default or another device. Never silently switch microphones during a recording.

Use the selected output only for playback. The microphone test displays levels but never routes live microphone audio to the output, avoiding feedback and echo. Audible live monitoring can be a settings-controlled future option.

### 7.3 Waveform and timeline

Do not retain every sample in memory. Calculate min/max peak pairs in multiple resolution levels as samples arrive. Persist a compact waveform cache per segment so reopening a session is fast.

The waveform control should:

- Concatenate segment peak data visually with no fake gaps
- Render the center line, amplitude peaks, time ruler, and playhead
- Render only visible points and decimate according to viewport width
- Coalesce live updates to approximately 10 frames per second, independent of capture callbacks
- Support click/touch seeking while stopped or playing
- Support mouse/touch drag selections that shade a range and display its start, end, and selected duration; destructive editing remains out of scope
- Keep the center line visually static while live amplitude peaks continue to appear
- Keep the complete selected clip fitted to the waveform; omit inactive zoom controls until a real zoom interaction is implemented
- Expose an accessible text summary such as duration and peak level because a custom waveform is primarily visual

Use a custom WPF control drawing into a retained bitmap or drawing visual rather than thousands of individual XAML elements.

### 7.4 Playback

Create a logical concatenating sample provider over all finalized segments in the selected clip. Playback uses the selected WASAPI output endpoint. Seeking maps a logical clip timestamp to a segment plus offset. The UI playhead follows the audio clock, not a UI timer's accumulated estimate.

Playback must release or safely share devices before capture begins. A single AudioCoordinator owns capture and playback so conflicting states cannot exist.

### 7.5 MP3 and lossless WAV export

For each non-empty clip:

1. Open its segments in order as one canonical PCM stream.
2. Feed 48 kHz, mono, 16-bit PCM into the Media Foundation MP3 encoder.
3. Encode at 128 kbps CBR by default; reject unsupported configured profiles instead of silently changing quality.
4. Write to a staging `.mp3.partial` file on the destination.
5. Stream the same canonical segments into a staged PCM WAV under `Raw`, without decoding the MP3 or changing sample quality.
6. Close and reopen both outputs to verify the MP3 is readable and the WAV is exactly 48 kHz, 16-bit, mono PCM with the expected data length.
7. Rename to the final `.mp3` and `.wav` names without overwriting existing data.
8. Write and durably flush `.voicebooth-export.json` before committing the session directory; use it to identify an already-committed export after restart.

The Microsoft encoder supports 16-bit PCM input, mono/stereo, 48/44.1/32 kHz, and standard MP3 bitrates. Add an installation/startup diagnostic that proves the encoder is available, especially on Windows N editions or modified enterprise images.

## 8. File naming and collision rules

Keep display names separate from filesystem-safe names. Sanitization should:

- Replace Windows-invalid characters and control characters with `-`
- Collapse repeated whitespace and separators
- Trim trailing spaces and periods
- Reject reserved device names such as `CON`, `PRN`, `AUX`, `NUL`, and `COM1`
- Cap each safe component at a documented length, proposed 80 characters
- Fall back to `Session` or `Clip-N` if sanitization empties the value

Use the session's local start date for every filename, even when a session crosses midnight.

Given session `Jordan-Election` started July 16, 2026:

| Item | Proposed result |
| --- | --- |
| Session folder | `Jordan-Election_07162026` |
| Unnamed clip 1 | `Jordan-Election_01_07162026.mp3` |
| Unnamed clip 2 | `Jordan-Election_02_07162026.mp3` |
| Clip named `Intro` | `Jordan-Election_Intro_07162026.mp3` |
| Second clip also named `Intro` | `Jordan-Election_Intro_02_07162026.mp3` |
| Existing same-day session folder | `Jordan-Election_07162026_02` |

Each MP3 has the same basename with a `.wav` extension under the session folder's `Raw` subfolder.

Never overwrite. Reserve all final names before export so a mid-export collision cannot change only part of the naming set. The ordinal remains stable after deletion; deleting clip 2 does not renumber later clips and surprise the user.

## 9. Local autosave and crash recovery

### 9.1 Storage locations

Use separate locations by responsibility:

```text
%ProgramFiles%\VoiceBoothKiosk\
  application binaries (read-only to kiosk user)

%ProgramData%\VoiceBoothKiosk\
  config\machine-settings.json
  config\content.json
  config\assets\<managed station and preset logos>
  logs\

%LocalAppData%\VoiceBoothKiosk\
  state\user-settings.json
  work\<session-guid>\
    session.json
    segments\<clip-guid>\<segment-guid>.wav
    peaks\<segment-guid>.peaks
    trash\
  quarantine\

<approved destination>\<session-folder>\
  <one verified MP3 per non-empty clip>
  Raw\
    <one verified lossless WAV per non-empty clip>
  .voicebooth-export.json (hidden commit receipt; no audio samples)
```

Machine settings and content are written by the configuration editor running as the current kiosk user. When the default `%ProgramData%` location is deployed, grant that account modify access to `%ProgramData%\VoiceBoothKiosk\config`; no elevation is required. The kiosk account also writes its local work area and appropriately ACLed logs. If policy requires recovery across replacement kiosk accounts, move the work root to a dedicated ACLed `%ProgramData%` subfolder instead.

### 9.2 Durability rules

- Save JSON via write-new, flush, and atomic replace; retain one known-good backup.
- Include `schemaVersion`, application version, and checksums/status for segment files.
- Save metadata after every start/stop, rename, create, delete, selection, destination, and important setting change.
- Finalize segment headers on every Stop; scan and repair only an interrupted `.partial` file at recovery.
- Accept interrupted WAV audio only when its canonical format and non-empty block-aligned PCM payload validate; repair stale RIFF/data lengths before promotion.
- Check free space before recording and continuously during long recordings.
- Warn at a configurable threshold; stop gracefully before the disk is exhausted.
- Never put active recording files on a network share or removable destination.
- Flush the export receipt before the final directory move so destination commit is restart-idempotent.

### 9.3 Startup recovery and privacy timeout

At startup, scan only known work directories and manifests. If an active session exists:

- Validate completed segments and recover a structurally repairable interrupted WAV.
- Detect a matching committed-export receipt across approved destinations, validate its MP3s and Raw WAVs, and finish local completion without re-exporting.
- Show `A recording session was interrupted` with Resume and Start New choices.
- Require explicit confirmation before Start New quarantines the old session.
- Do not expose previous names/audio on the public welcome screen longer than necessary.
- Keep quarantined sessions available for administrator-controlled recovery rather than deleting them at timeout.

While an active session is Ready, Playing, or Paused, the implementation starts a visible 60-second warning after the configured inactivity interval. User activity cancels the warning. Expiry quarantines the recoverable session and returns to Welcome, preventing the next walk-up user from seeing or playing the previous user's work.

Automatic retention must never silently destroy the only copy. The implemented default retains completed local recovery data for 24 hours before startup cleanup; quarantined or incomplete sessions are preserved for administrator-controlled recovery.

## 10. Configuration and access model

### 10.1 Configuration categories

| Category | Examples |
| --- | --- |
| Branding | App title/subtitle, managed local logo asset, visual and hexadecimal color controls, type scale, background, station identity |
| Content | Welcome headline, two stacked welcome supporting blocks, session-name help, recording instructions, device help, end-session text, privacy notice; actual newlines and literal `\n` are rendered as line breaks |
| Layout policy | Left/right clip rail, text-block visibility, compact/standard/large controls, waveform colors |
| Audio | Canonical format, MP3 bitrate, default endpoint IDs, allow device changes, stereo policy, level thresholds |
| Destinations | Approved roots, friendly labels, default destination, minimum free space, browse permission |
| Session behavior | Inactivity warning, recovery retention, default clip naming, confirmation policies |
| Kiosk behavior | Full-screen, always-on-top policy, close handling, cursor behavior, welcome/stopped-session Settings entry |
| Accessibility | High-contrast theme, text scale, reduced motion, audible status cues |
| Diagnostics | Log level, log retention, hardware self-test settings |

Instructions should be reusable content blocks referenced by screen/region IDs, not hardcoded strings. Support plain text plus a constrained subset of formatting. Do not load arbitrary XAML, HTML, scripts, or remote URLs from configuration.

### 10.2 Persistence and validation

- Version all JSON schemas and migrate forward on startup.
- Validate into a typed settings object before applying anything.
- Reject invalid values with precise editor-facing errors and keep the last known-good configuration.
- Save all changes atomically.
- Store endpoint IDs plus friendly-name fallbacks; never persist list indexes.
- Store paths as normalized absolute paths after editor validation.
- Copy selected logos into machine-managed configuration assets before storing their path, so branding never depends on removable or remote source media remaining available.
- Keep secrets out of JSON. If a local credential is ever needed, use Windows DPAPI/Credential Manager.
- Provide validation, apply, destination browsing/checking, Restore Defaults, and portable preset import/export in the settings editor.
- Preset ZIPs contain the complete typed settings payload plus an optional embedded logo. Ignore machine paths embedded in a manifest, install only the known size-limited logo entry into managed assets after SHA-256 verification, and require an explicit Save and apply after import.

### 10.3 Access strategy

Use two executables so recording and configuration remain operationally separate:

1. `VoiceBoothKiosk.exe` is the public recorder. Its welcome screen exposes a small top-right Settings icon that launches the adjacent editor.
2. `VoiceBoothKiosk.Config.exe` runs as the current user (`asInvoker`) and writes the validated configuration. It deliberately has no PIN, password, hidden gesture, or elevation requirement.

The public app may also expose a limited Audio Devices panel if `allowPatronDeviceSelection` is true. When false, devices remain fixed during a recording session but remain configurable through Settings. Optional Windows Shell Launcher, AppLocker/WDAC, and Keyboard Filter policies still form the operating-system kiosk boundary; they do not add an application-level password to Settings.

## 11. Application architecture

Proposed solution structure:

```text
VoiceBoothKiosk.sln
src/
  VoiceBoothKiosk.App/             WPF views, view models, navigation, startup
  VoiceBoothKiosk.Domain/          Session, Clip, Segment, naming and state rules
  VoiceBoothKiosk.Application/     Use cases and orchestration interfaces
  VoiceBoothKiosk.Audio.Windows/   NAudio capture, playback, conversion, waveform peaks
  VoiceBoothKiosk.Infrastructure/  JSON, filesystem, export, logging, recovery
  VoiceBoothKiosk.Config/          Ungated configuration editor
tests/
  VoiceBoothKiosk.Domain.Tests/
  VoiceBoothKiosk.Application.Tests/
  VoiceBoothKiosk.Infrastructure.Tests/
  VoiceBoothKiosk.Audio.Tests/
  VoiceBoothKiosk.UiAutomation.Tests/
deployment/
  installer/
  kiosk/                           Shell Launcher/AppLocker/Keyboard Filter templates
docs/
```

Important interfaces:

| Interface/service | Responsibility |
| --- | --- |
| `IAudioCaptureService` | Enumerate/open/stop an input and emit canonical samples/status |
| `IAudioPlaybackService` | Play/pause/seek a logical clip through a chosen output |
| `IAudioDeviceService` | Endpoint discovery, notifications, stable ID resolution, test operations |
| `IWaveformService` | Produce and load multiresolution peak caches |
| `ISessionRepository` | Atomic manifest persistence and recovery discovery |
| `IWorkingStorage` | Segment lifecycle, capacity checks, trash/quarantine cleanup |
| `IExportService` | Reserve names, stage, encode, validate, commit, and report progress |
| `IConfigurationService` | Load, validate, migrate, snapshot, and observe approved changes |
| `IClock` | Consistent local/UTC timestamps and testability |

Use a single explicit `AudioCoordinator` state machine. Do not let individual view models independently start NAudio objects. Route long operations through cancellable background tasks while marshaling only small state updates to the UI thread.

## 12. Kiosk deployment and security

### 12.1 Application full-screen behavior

The app itself should:

- Use a borderless maximized window sized to the intended kiosk monitor
- Recover focus after allowed dialogs close
- Handle Alt+F4 as a policy-controlled request rather than exiting in patron mode
- In supervised mode, keep a hidden parent supervisor alongside the visible recorder; do not use a detached one-shot launcher
- Have the supervisor restore/foreground the visible recorder every two minutes and relaunch it after an unexpected exit
- Reserve a distinct intentional-exit code for the protected four-second X so it can stop the supervisor without a relaunch
- Detect display/DPI changes and retain a usable layout at 100-200% scaling
- Have a watchdog-friendly nonzero exit code for unrecoverable startup failures
- Never require administrator elevation at runtime

### 12.2 Windows lockdown

Recommended production baseline:

- Windows 11 Enterprise, Enterprise LTSC, Education, or IoT Enterprise because those editions support Shell Launcher
- Dedicated, automatically signed-in, non-administrator local/domain kiosk account
- Shell Launcher v2 configured to run `VoiceBoothKiosk.exe` and restart it on unexpected exit
- AppLocker/Windows Defender Application Control allowlist for permitted binaries
- Keyboard Filter for escape combinations, with a documented administrator breakout key
- Block or policy-control removable media, settings, task switching, notifications, sleep, updates/restarts, and network access as operational requirements dictate
- BitLocker for local recordings at rest
- Separate administrator account for maintenance
- Code-signed application and installer

Shell Launcher replaces Explorer but Microsoft explicitly notes that it does not itself prevent access to every other desktop component. The application, Shell Launcher, allowlisting, account permissions, and device policies must be tested as a system.

If the hardware runs Windows Pro, the app can still be full-screen and auto-started, but that should be labeled a supervised/full-screen deployment rather than a hardened kiosk. Assigned Access single-app kiosk does not directly host an arbitrary WPF desktop app; its restricted-user configuration or an edition upgrade would be needed.

## 13. Accessibility and usability requirements

- Minimum 48x48 effective-pixel touch targets; make primary controls larger
- Full keyboard navigation with visible focus and logical tab order even if the deployed kiosk is touch-first
- Automation names/help text for every control and a custom AutomationPeer for the waveform where useful
- Text plus shape/icon/status for recording, errors, and selection; never color alone
- Contrast that meets WCAG 2.2 AA as a design baseline
- Configurable text size and high-contrast theme; respect Windows high-contrast settings
- Reduced animation option
- Clear, plain-language confirmations that name the affected session/clip
- Audible cues optional and disabled/configurable if they could leak into the microphone
- No tiny hover-only actions; rename and delete remain touch accessible
- Test with mouse, keyboard, touchscreen, Windows Narrator, and 1366x768 through 4K/DPI-scaled displays

## 14. Logging, privacy, and supportability

Use structured rolling logs with event IDs and bounded retention. Log application version, state transitions, endpoint IDs/friendly names, durations, byte counts, export outcomes, and exception details. Do not log audio samples. Hash or redact session/clip names by default because they may contain personal information.

Provide an operator diagnostics screen/export bundle containing:

- App and OS version
- Sanitized configuration
- Audio device inventory and formats
- Encoder availability/self-test result
- Disk and destination checks
- Recent redacted logs
- No recordings unless an administrator explicitly includes a chosen file

The UI should always translate low-level audio/COM/I/O errors into an actionable message while retaining technical detail in logs.

## 15. Reliability requirements

- A completed Stop must survive immediate process termination and restart.
- A power loss during recording may lose only the active buffered tail; all earlier finalized segments remain recoverable.
- An export failure must not damage working audio or overwrite destination files.
- Unplugging the microphone must stop capture, preserve completed data, and present recovery choices.
- The app must remain responsive during at least a configured maximum clip/session duration; propose testing 4-hour clips and 8-hour sessions.
- Memory use should depend on waveform viewport/cache size, not total sample count.
- Recording must be local-first even when the final destination is a UNC path.
- On controlled shutdown/logoff, request a safe stop and save; still tolerate forced termination.
- Run a startup self-test for configuration, work directory, free space, audio endpoints, and MP3 encoder before enabling Start Session; expose failures on demand without adding healthy-state clutter to the welcome screen.

## 16. Testing strategy

### 16.1 Automated tests

- Domain state transition tests for every allowed and rejected command
- Filename sanitization, reserved-name, duplicate, long-path, Unicode, and collision tests
- Config schema validation and migration tests
- Atomic-save and last-known-good fallback tests
- Export transaction tests using fake encoder/filesystem failure injection
- Segment concatenation, seek mapping, duration, and sample format tests
- Waveform peak calculation tests with deterministic generated signals
- View-model command availability and error presentation tests
- WPF UI Automation smoke tests for the primary walk-up flow

### 16.2 Hardware/integration matrix

Test at minimum:

- Target USB microphone/interface and a generic USB microphone
- Built-in audio, USB headphones, and unplug/replug scenarios
- Device renamed, default device changed, device disabled, and endpoint ID unavailable
- 44.1/48/96 kHz hardware mix formats, mono and stereo devices
- Silence, clipping, very low input, and sustained recording
- Local, removable, and UNC destinations if each is approved for production
- Network interruption and destination disconnect during export
- Low disk, read-only folder, long path, duplicate names, antivirus scanning, and power interruption
- Touchscreen plus physical/on-screen keyboard
- Kiosk restart, Windows reboot, forced process kill, and interrupted export recovery

### 16.3 Performance targets

Initial measurable targets:

- Ready screen within 5 seconds on target hardware after process launch
- Record indication visible within 150 ms of a successful capture start
- No audio dropouts under target hardware's normal peak load
- Live waveform latency below 150 ms without blocking capture
- UI interactions below 100 ms except explicitly asynchronous operations
- Memory below an agreed ceiling after an 8-hour session; determine the ceiling from target hardware during the spike
- Export progress remains responsive and cancelable before commit

## 17. Delivery plan and implementation status

The repository contains the version 1 implementation described in Phases 0-4, including the recorder, configuration application, recovery, transactional export, tests, publish script, and kiosk guidance. The phase breakdown below remains useful as the delivery history and as a checklist. Hardware-specific validation, signing/installer work, Windows policy rollout, accessibility testing, long-duration soak testing, and the supervised pilot remain site acceptance work.

### Phase 0: decisions and audio spike (3-5 engineering days)

- Confirm Windows edition, kiosk hardware, microphone/interface, destination types, and maximum durations
- Prove NAudio WASAPI capture through canonical conversion to a crash-recoverable WAV
- Prove selected-device playback and device invalidation behavior
- Prove Media Foundation 48 kHz mono 128 kbps MP3 export and enumerate available profiles on the production Windows image
- Render a performant live waveform for at least a simulated 8-hour clip

Exit gate: a command-line or minimal-window vertical slice records, pauses, appends, plays, exports, and recovers on target hardware.

### Phase 1: core recorder MVP (2-3 weeks)

- Solution structure, domain state machine, session start, clip list, naming
- Record/pause/resume, play/pause-to-edit/seek, new take, new clip, rename, delete/undo
- Live meter, waveform/timeline, audio device selection
- Local session manifests and basic startup recovery

Exit gate: the full primary workflow works offline in a normal desktop window with no known audio-loss defect.

### Phase 2: transactional export and resilience (1-2 weeks)

- Destination presets and validation
- Naming/collision reservation
- Staged MP3 export, verification, retry, and cleanup
- Low-disk handling, unplug/replug, forced termination, interrupted WAV repair
- Structured logs and diagnostics

Exit gate: failure-injection and reboot tests demonstrate no overwrite and no loss of previously stopped audio.

### Phase 3: customizable UI and settings editor (1-2 weeks)

- Content blocks, theming, layout policies, accessibility settings
- Typed/versioned settings, migrations, atomic updates, preview/import/export
- Separate ungated configuration editor launched from the welcome screen
- Destination add/browse/check/default controls and patron device-choice policy

Exit gate: a user can brand and configure the app, including save destinations, without rebuilding it or supplying administrator credentials.

### Phase 4: kiosk packaging and hardening (1 week)

- Self-contained signed release and installer
- Shell Launcher, AppLocker, Keyboard Filter, auto-logon, breakout, and recovery templates
- Full-screen/focus/restart behavior
- Operational deployment, rollback, backup, update, and service documentation

Exit gate: a standard walk-up user cannot exit the intended experience using the agreed test checklist, while an administrator can service it using the documented path.

### Phase 5: pilot and production acceptance (1-2 weeks elapsed)

- Run supervised real sessions
- Measure audio levels, user errors, export time, and recovery incidents
- Refine copy, touch sizing, default audio settings, and support instructions
- Complete accessibility and long-duration soak tests

Expected first-production range: approximately 6-9 engineering weeks plus procurement/policy lead time. Scope additions such as editing, remote management, transcription, or cloud delivery should be scheduled separately.

## 18. Release acceptance criteria

A release is ready when all of the following are demonstrated on the production image and hardware:

1. A new user can name a session and record the first clip without instruction from an operator.
2. Pause Recording, playback, seek, and resumed recording produce one continuous logical clip.
3. New Clip, rename, delete/undo, select, and New Take behave according to the state rules.
4. Input and output device choices persist and device removal fails safely.
5. A forced process kill after Pause Recording recovers every finalized clip.
6. End Session creates correctly named playable MP3 files and matching validated lossless WAVs under `Raw` without overwriting prior output.
7. Failed export leaves the entire session retryable locally.
8. The success/reset flow exposes no previous user data to the next user.
9. Configuration persists and validates, the welcome-screen Settings icon opens the ungated editor without elevation, and a preset ZIP round-trips the complete setup and embedded logo between stations.
10. The Windows kiosk deployment restarts the app after failure and passes the agreed escape/lockdown test.
11. The main flow passes touch, keyboard, DPI, Narrator, long-duration, and low-disk tests.
12. Installation, update, rollback, servicing breakout, recovery, and log collection are documented.

## 19. Deployment decisions to confirm before production

The implemented baseline uses 48 kHz, 16-bit mono working audio, an exact 128 kbps Media Foundation MP3 profile by default, and a hidden export commit receipt. Production still requires these site-specific decisions:

1. Which Windows 11 edition is installed? Enterprise/IoT Enterprise is strongly preferred for Shell Launcher.
2. What exact input interface/microphone and output hardware will be used?
3. Are final destinations local folders, removable drives, UNC shares, or a fixed list containing several types?
4. Should devices also be selectable in the recording workspace, or only through the ungated Settings editor?
5. What are the expected maximum clip/session durations and minimum free disk space?
6. Is the kiosk touch-first, physical-keyboard-first, or both? Is a custom safe on-screen keyboard needed?
7. What retention/privacy policy applies to interrupted, inactive, quarantined, and successfully exported local audio?
8. Should ending a session require a staff code, or should every walk-up user be able to select a destination and export?

Until changed, the plan assumes Windows 11 Enterprise x64, a single USB microphone, headphones, mono voice, touch plus keyboard, approved destination presets including a possible UNC path, and no automatic destruction of the only recording copy.

## 20. Future extensions

- Loudness measurement/normalization as a non-destructive export option
- Noise gate or high-pass filter with controlled presets
- Multiple takes retained per clip rather than New Take replacing the current take
- Markers and notes on the timeline
- Staff handoff/approval before export
- Remote health dashboard and configuration deployment
- Automatic transcription and metadata sidecars
- Upload to newsroom asset storage
- Multi-language instruction packs

The domain boundaries above deliberately leave room for these features without making them dependencies of the first reliable recorder.

## 21. Primary technical references

- [.NET releases and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support) - .NET 10 is the current LTS release and is supported through November 2028.
- [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) - WPF is the Windows-only .NET desktop UI framework used here.
- [NAudio repository](https://github.com/naudio/NAudio) and [NAudio NuGet package](https://www.nuget.org/packages/NAudio) - Windows capture/playback, device, conversion, metering, and Media Foundation capabilities; 2.3.0 is the current stable package at planning time.
- [Windows Core Audio APIs](https://learn.microsoft.com/en-us/windows/win32/coreaudio/about-the-windows-core-audio-apis) - endpoint enumeration and WASAPI foundations.
- [Recovering from an invalid audio device](https://learn.microsoft.com/en-us/windows/win32/coreaudio/recovering-from-an-invalid-device-error) - required unplug/reconfiguration handling.
- [Media Foundation MP3 encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/mp3-audio-encoder) - supported PCM inputs, sample rates, channels, and bitrates.
- [Shell Launcher overview](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/) - Win32 custom-shell support, supported editions, and limitations.
- [Assigned Access overview](https://learn.microsoft.com/en-us/windows/configuration/assigned-access/) - why single-app Assigned Access is not the direct deployment model for WPF.
- [Keyboard Filter](https://learn.microsoft.com/en-us/windows/configuration/keyboard-filter/) - kiosk key suppression and supported editions.
- [WPF custom-control UI Automation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/ui-automation-of-a-wpf-custom-control) - accessibility and automated-test support for the waveform.
- [`dotnet publish`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish) - self-contained Windows deployment options.
