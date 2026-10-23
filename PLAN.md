# DuckDuck — Implementation Plan

Reference: `SPEC.md`. Target: one working exe, ~700 lines, 9 files, 1 NuGet package.

## Files

```
DuckDuck/
  DuckDuck.csproj        net10.0-windows, UseWPF, UseWindowsForms, single-file publish props
  App.xaml               dark brushes, slider/button/combobox styles
  App.xaml.cs            startup: load config, start Engine, tray icon, --selftest, restore on exit
  MainWindow.xaml        layout from the spec mockup
  MainWindow.xaml.cs     snapshot → meters/status, picker popup, sliders → config
  AudioSessions.cs       Core Audio: scan devices/sessions, group by process name, peak, get/set volume
  Ducker.cs              pure state machine + curves (no COM, testable)
  Engine.cs              20 ms thread: AudioSessions + Ducker, publishes Snapshot
  Config.cs              record + Load/Save (System.Text.Json)
```

## Milestones

Each one ends with something runnable. Order is by risk: the audio layer is the only
thing that could sink the project, so it's spiked before any UI.

### M1 — Scaffold

- `dotnet new wpf -n DuckDuck`, add `NAudio.Wasapi`.
- csproj: `net10.0-windows`, `UseWPF`, `UseWindowsForms`, `Nullable`, `ApplicationIcon`,
  `PublishSingleFile`, `SelfContained`, `RuntimeIdentifier=win-x64`.
- `App.xaml`: brushes from the spec colour table, flat styles for Slider, Button,
  ComboBox, CheckBox.
- Dark title bar P/Invoke in `MainWindow` `OnSourceInitialized`.
- **Done when:** empty dark window opens; `dotnet publish` yields one exe that runs.

### M2 — Audio layer spike (`AudioSessions.cs`)

- `Scan()`: for each active render device → `AudioSessionManager.RefreshSessions()` →
  iterate sessions. Skip PID 0 (system sounds) and expired state. Resolve PID →
  process name via `Process.GetProcessById(pid).ProcessName`, cached per PID (cache
  checked against the process start time: Windows reuses PIDs within seconds).
  Group into `Dictionary<string, List<AudioSessionControl>>` keyed by process name.
- `Peak(name)` = max `ProcessMeter.Peak()` over the group's processes; session
  `MasterPeakValue` only when no process could be metered (see v1.0.1 below).
- `GetVolume(name)` = first session's `SimpleAudioVolume.Volume`.
- `SetVolume(name, v)` → every session in the group.
- `ExePath(name)` for icons: `Process.MainModule.FileName`, swallow access errors → null.
- Rescan every 2 s, or sooner when `OnSessionCreated` fires (sets a flag).
- Dispose dropped sessions.
- **Done when:** a temporary debug loop prints `Spotify 0.31  Discord 0.00  brave 0.12`
  while audio plays, and setting Spotify to 0.2 visibly moves the Windows mixer slider.
  This is the go/no-go check; everything after is plumbing.

### M3 — Ducker (`Ducker.cs`)

- Pure: `Step(dtMs, sourceLevel, currentVol) → (State, float? writeVol)`.
  `null` write means "idle, don't touch".
- States: Idle, Attack, Ducked, Release. Hold timer. Retrigger from Release keeps
  current volume as the attack's `from`.
- Curves as a `static float Ease(Curve c, float t)` switch.
- `Settings` record passed in; swapping it mid-run is fine (next step uses new values).
- `--selftest` flag in `App.xaml.cs` runs asserts and exits:
  - idle stays idle below threshold
  - level above threshold → Attack, reaches `duckTo` after `attackMs`
  - quiet for < hold → still Ducked; quiet for ≥ hold → Release
  - Release reaches rest after `releaseMs`
  - retrigger mid-release attacks from current volume (no discontinuity)
  - rest < duckTo → never raises
- **Done when:** `DuckDuck.exe --selftest` exits 0.

### M4 — Engine (`Engine.cs`)

- Dedicated MTA thread, `Stopwatch` loop, 20 ms sleep.
- Owns `AudioSessions` + `Ducker`. Reads `Config` via a `volatile` reference the UI swaps.
- Each tick: maybe rescan → source level → ducker step → write volume if asked → build
  `Snapshot` (per-app peak, per-app running?, target volume, state) → publish via
  `volatile` reference.
- In Idle, reads target volume into `rest`. On Attack start, writes `ducking:true` +
  rest to config; on Release end, clears it.
- `Stop()`: restore rest, join thread. Called from app exit and
  `AppDomain.UnhandledException`.
- **Done when:** with sources/target hardcoded in config.json, Spotify ducks under
  Discord with no UI.

### M5 — UI (`MainWindow.xaml` / `.cs`)

- Layout per spec mockup: two `Border`s (4 px, amber / blue), arrow glyph between, four
  slider rows, two curve combos, checkbox.
- `DispatcherTimer` 33 ms: read `Snapshot` → update meters (`Rectangle` width), target
  volume bar + %, status pill text/dot, target border brightness.
- Source rows: icon (from `ExePath`, cached `ImageSource`), name, meter, × button.
- Picker: `Popup` listing `Snapshot` apps minus chosen ones, icon + name + meter.
  Click → add to sources or set target → save config → swap into engine.
- Sliders/combos → `Config` → debounce 300 ms → save + swap.
- Tray: `NotifyIcon` in `App.xaml.cs`; close hides, double-click shows, menu Open/Quit.
- Glyph font: `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"`.
- **Done when:** the full flow works from a fresh config.json with no editing.

### M6 — Persistence, recovery, publish

- `Config.Load()` defaults on missing/corrupt file. `Save()` writes atomically
  (temp + move).
- Launch recovery: `ducking:true` → restore `restVolume` once target session exists.
- `startMinimized` honoured.
- `publish.cmd` → `dotnet publish` single-file, copies exe to `dist/`.
- Manual test matrix (below).

## Manual test matrix

| # | Scenario | Expect |
|---|---|---|
| 1 | Discord voice + Spotify | Spotify ducks within attack time, returns after hold + release |
| 2 | Brave YouTube + Spotify | Same |
| 3 | Short Discord notification blip | Brief duck, hold prevents flutter |
| 4 | Spotify not running → start it | Picked up within 2 s, status changes from Waiting |
| 5 | Change Spotify in Windows mixer while idle | New level becomes rest |
| 6 | Quit while ducked | Spotify restored to rest |
| 7 | Kill via Task Manager while ducked, relaunch | Spotify restored to rest |
| 8 | Discord on headset, Spotify on speakers | Still ducks |
| 9 | Set duck-to higher than rest | Volume never rises |
| 10 | Close window | Lives in tray, still ducking |

## Decisions to confirm at review

1. **Duck-to is an absolute mixer %** (Spotify goes *to* 20%), not relative (−12 dB from
   wherever it is). Spec says absolute. Relative is a one-line change if preferred.
2. **Self-contained single exe (~70 MB)** vs framework-dependent (~1 MB, needs .NET 10
   Desktop Runtime installed). Spec says self-contained.
3. **Run at Windows startup** checkbox: one registry Run key, trivial. Not in v1 scope
   unless you want it.
4. **Mic as a trigger** (duck when *you* talk, not just when Discord outputs): added
   after 1.0.1 as the `Microphone` pseudo-source, the default communications capture
   endpoint's meter. Raw level before Discord's gate, and only live while an app records.

## Skipped on purpose

- MVVM / view models: one window, code-behind is fine.
- Profiles, multiple rules, hotkeys, installer, auto-update.
- dB-space interpolation: linear mixer units + curves first; revisit if it feels wrong.
- Device-change notifications: 2 s rescan covers it.

## v1.0.1 — Discord's meter mirrors everything else (found 2026-10-22)

**Symptom:** Brave (target) playing YouTube moves the Discord (source) meter, so Brave ducks
itself in a loop.

**Cause (measured, not guessed):** Discord's voice engine lives in its *renderer* process
(PID with `--type=renderer`) and holds a WASAPI **loopback capture** on the output device
(echo-cancel reference / stream audio). That stream sits in the same audio session as its
voice output, and `IAudioMeterInformation` on a session counts every stream in it, so
Discord's session meter equals the endpoint mix: with nobody talking it reads exactly
Brave's level, sample for sample (0.055 / 0.055). Not a grouping bug; any app that
loopback-captures (Discord, OBS, Steam Remote Play, GeForce overlay) does this.

**Fix:** meter apps with **Windows process loopback** instead of session meters.
`ActivateAudioInterfaceAsync` on the process-loopback virtual device with
`AUDIOCLIENT_ACTIVATION_PARAMS { pid, INCLUDE_TARGET_PROCESS_TREE }` yields a capture
stream of exactly what that process tree renders, on any device; the target's own capture
streams are not in it. Prototype verified on this machine: Discord's process meter reads
0.000 while its session meter shows Brave's 0.055; Brave's and WoW's process meters match
their session meters.

- `ProcessMeter.cs` (new, ~80 lines): own interop for `ActivateAudioInterfaceAsync` and
  its completion handler; NAudio's public `AudioClient(IAudioClient)` and
  `AudioCaptureClient` do the rest. `Peak()` = max |sample| drained since the last call.
  `TryCreate` returns null when activation fails (Windows before 10 2004) → session meter.
- `AudioSessions.cs`: one `ProcessMeter` per PID, owned by the PID cache. `Peak(name)` =
  max over the app's processes; session meters only when no process has a meter. Also
  check the PID cache against the process start time: Windows reuses a dead PID within
  seconds, and a stale entry would pin a meter to the wrong app.
- `Engine.cs`: build `Apps()` once per tick and read source peaks from it (a process meter
  drains its buffer, so it must be read exactly once per tick).
- `App.ListApps`: run on an MTA thread; the activation callback deadlocks against a blocked
  STA thread.
- SPEC / README / PLAN: the metering sentences.

Decisions to confirm:
1. Process loopback needs Windows 10 2004+; older Windows silently keeps today's behaviour.
2. Meter *all* apps this way (UI meters stay truthful) rather than only sources (fewer
   capture streams). Plan says all apps; ~10 shared-mode capture streams is nothing.
