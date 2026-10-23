# DuckDuck — Spec

One-window Windows app. Pick **source** apps and one **target** app. When any source
plays audio, the target's volume is lowered (ducked) to a set level. When the sources go
quiet, it comes back. Attack/release times and curves shape the ramp.

Example: sources = Discord, Brave. Target = Spotify. Someone talks in Discord → Spotify
drops to 20% → they stop → Spotify fades back up.

## Non-goals (v1)

- No microphone trigger. Sources are apps that *output* audio.
- No profiles / multiple rules. One set of sources, one target.
- No installer. One `.exe`.
- No EQ, no routing, no per-device settings.

## Vocabulary

| Term | Meaning |
|---|---|
| Source | App whose audio output triggers ducking (Discord, Brave) |
| Target | App that gets ducked (Spotify) |
| Session | A Windows audio session: one per process per output device |
| Rest volume | Target's mixer volume when not ducked (whatever the user set in Windows) |
| Duck volume | Mixer volume the target is held at while ducked |

## How it works on Windows

Windows Core Audio (WASAPI session API). For every active output device, enumerate
audio sessions. Each session exposes:

- process ID
- a peak meter (`IAudioMeterInformation`) — fallback only, see *Levels* below
- a volume (`ISimpleAudioVolume`) — the same slider shown in the Windows Volume Mixer

Ducking writes that per-session mixer volume. It never touches the app's own volume
control, and it's visible in the Windows mixer, so nothing is hidden from the user.

Apps are identified by **process name** (`Discord`, `brave`, `Spotify`). Chromium /
Electron apps create sessions under child processes that share the parent's name, so
all sessions with a matching name are grouped as one app. Sessions on *all* output
devices are included, so Discord on a headset + Spotify on speakers works.

**Levels** come from Windows **process loopback** (`ActivateAudioInterfaceAsync` on the
process-loopback virtual device, Windows 10 2004+): one capture stream per process that
owns a session, carrying exactly what that process renders, on any device. The session
meter is not used because it also counts the session's own loopback *captures*: Discord's
voice engine keeps one open on the output device, so its session meter mirrors the whole
mix and would duck the target whenever the target itself plays. Session meters are the
fallback when activation fails (older Windows).

**Microphone** is offered as a pseudo-source: the endpoint meter of whichever capture
device some app is currently recording from (max over them), so it follows the mic Discord
uses and is live exactly during a call. DuckDuck never opens a mic itself: that flips a
Bluetooth headset into its low-quality hands-free profile and can trigger Windows'
communications ducking. While no app records, the source is shown as idle. It is the raw
level before Discord's noise gate, and it cannot be a target.

## Behaviour

Engine ticks every 20 ms:

1. Each source's **level** = max process-loopback peak over that app's processes.
2. **Active** = any source whose level ≥ *its own* threshold (Discord can trigger at
   15 % while Brave triggers at 5 %).
3. State machine:

   ```
   Idle ──active──▶ Attack ──done──▶ Ducked ──quiet for Hold ms──▶ Release ──done──▶ Idle
                      ▲                                               │
                      └─────────────── active again ──────────────────┘
   ```

   Re-trigger during Release restarts Attack *from the current volume* (no jump).
   Going quiet during Attack still finishes the attack, then counts Hold.

4. **Attack**: ramp target volume from current → duck volume over Attack ms using the
   Attack curve. **Release**: ramp current → rest volume over Release ms using the
   Release curve.
5. **Idle**: engine *reads* the target's volume and treats it as rest. User changes in
   the Windows mixer while idle become the new rest volume. Engine never writes in Idle.
6. **Duck volume = min(rest, duckTo).** Ducking never raises volume.
7. Target not running → engine idles and watches for it. Source not running → ignored.

### Curves

Easing of `t ∈ [0,1]` → progress `p`. Volume = `from + (to − from) · p`, in mixer
units (linear 0–1, same as the Windows slider).

| Name | Formula | Feel |
|---|---|---|
| Linear | `p = t` | constant rate |
| Fast | `p = 1 − (1−t)²` | moves quickly, then settles (good attack default) |
| Slow | `p = t²` | eases in, then moves |
| Smooth | `p = t²(3 − 2t)` | S-curve, gentle both ends (good release default) |

### Settings

| Setting | Default | Range |
|---|---|---|
| Duck to | 20 % | 0–100 % |
| Attack | 150 ms | 0–5000 |
| Attack curve | Fast | Linear / Fast / Slow / Smooth |
| Release | 800 ms | 0–10000 |
| Release curve | Smooth | Linear / Fast / Slow / Smooth |
| Hold | 400 ms | 0–5000 |
| Threshold (per source, on its row) | 5 % | 0–50 % |
| Ducking enabled | on | off: a duck in progress releases normally, then nothing triggers |
| Start minimized to tray | off | |
| Run at Windows startup | off | HKCU Run entry `"<exe>" --tray`; the registry is the state, not config.json. Re-pointed to the current exe on every launch. |

Hold is the quiet time required before release begins. It stops the volume pumping
between words.

### Restore / recovery

- On quit (tray → Quit, or an unhandled exception) the target is set back to rest
  volume.
- Config stores `ducking: true` + rest volume the moment Attack starts, and clears it
  when Release finishes. On launch, if `ducking` is still true (app was killed while
  ducked) the target is restored to the saved rest volume. Otherwise the user's mixer
  would be left low and read back as the new rest.

## UI

Single window, ~760×480, resizable. Flat. Dark. No gradients, no shadows. Two big
bordered panels, an arrow between them, sliders below.

```
┌───────────────────────────────────────────────────────────────────────┐
│  🦆 DuckDuck                                        ● Listening       │
│                                                                       │
│  ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓            ┏━━━━━━━━━━━━━━━━━━━━━━━━┓   │
│  ┃ 🔊 SOURCES  when these play ┃            ┃ 🎧 TARGET   duck this   ┃   │
│  ┃                              ┃   ━━━▶    ┃                         ┃   │
│  ┃  [ico] Discord   ▮▮▮|▯▯  ✕   ┃   duck    ┃  [ico] Spotify          ┃   │
│  ┃     trigger at [━━●━━] 15 %  ┃            ┃        vol ▮▮▮▮▮▮▮▯ 72% ┃   │
│  ┃  [ico] Brave     ▯▯|▯▯▯  ✕   ┃            ┃                         ┃   │
│  ┃     trigger at [●━━━━]  5 %  ┃            ┃                         ┃   │
│  ┃  ＋ add app                   ┃            ┃  ⇄ change               ┃   │
│  ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛            ┗━━━━━━━━━━━━━━━━━━━━━━━━┛   │
│        amber, 4 px                                 blue, 4 px           │
│                                                                       │
│  DUCK TO    [━━━●━━━━━━━━━━━━]  20 %                                   │
│  ATTACK     [━●━━━━━━━━━━━━━━] 150 ms    curve [ Fast   ▾ ]            │
│  RELEASE    [━━━━●━━━━━━━━━━━] 800 ms    curve [ Smooth ▾ ]            │
│  HOLD       [━━●━━━━━━━━━━━━━] 400 ms                                   │
│                                                                       │
│  ☐ Start minimized to tray                                            │
└───────────────────────────────────────────────────────────────────────┘
```

### Colour

| Role | Hex |
|---|---|
| Window background | `#111214` |
| Panel fill | `#1A1B1E` |
| Text | `#E8E8E8` |
| Muted text | `#8B9099` |
| **Source** border, meters, status dot when ducking | `#F5A623` amber |
| **Target** border, volume bar | `#3DA9FC` blue |
| Slider track | `#2A2C31` |

Borders are 4 px, 10 px radius. While ducking, the target border brightens to full
`#3DA9FC` from the dimmed idle shade `#2A6A9C`. The status pill is one word:
**Listening** (target running, nothing playing), **Ducking** in blue with the live
target volume as a mono readout, **Idle** (no target, or target not running; the target
row dims to say which), **Off** (ducking disabled).

Numbers that change (ms, %, levels) use a mono readout face: JetBrains Mono, falling
back to Cascadia Mono then Consolas. Meters are 8 px pills on a `#2A2C31` trough. The
target's volume bar has a dim-blue ghost bar behind it at the rest volume, so you can see
where it will return to. Brand tokens and the duck mark live in `assets/brand/`.

Title bar uses Windows' immersive dark mode so it matches.

### Iconography

- Glyphs from **Segoe Fluent Icons** (ships with Windows 11; falls back to Segoe MDL2
  Assets). Speaker for SOURCES, headphones for TARGET, arrow between panels, plus for
  add, × for remove, swap for change. No icon packages.
- Per-app icons are extracted from the process executable. If that fails (elevated
  process), a generic app glyph is shown.
- Each source row has a live level meter with a tick at that source's threshold, and a
  "trigger at" slider under it. The target row shows its current mixer volume as a bar +
  percent, which visibly dips when ducking.

### Add-app picker

Clicking **＋ add app** (or **⇄ change** on the target) opens a popup listing every app
that currently has an audio session: icon, name, live meter. Already-chosen apps and
"System sounds" are excluded. An app can't be both a source and the target. Apps only
get a session once they've played something, so the picker says: *"Don't see your app?
Play something in it, then reopen."*

### Tray

Closing the window hides to the tray. Tray icon: left-click shows the window, right-click
→ **Enabled** (checkable, same setting as the window's "Ducking enabled") / **Show** /
**Exit**. The tooltip reads "DuckDuck (off)" while disabled. Settings persist immediately
on change. Launching the exe while it's already running (even hidden in the tray) just
brings the existing window up; there is never a second instance.

## Persistence

`%APPDATA%\DuckDuck\config.json`:

```json
{
  "sources": ["Discord", "brave"],
  "target": "Spotify",
  "duckTo": 0.20, "attackMs": 150, "attackCurve": "Fast",
  "releaseMs": 800, "releaseCurve": "Smooth",
  "holdMs": 400, "sourceThresholds": { "Discord": 0.15, "brave": 0.05 },
  "startMinimized": false,
  "ducking": false, "restVolume": 0.72
}
```

## Tech

| Piece | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (SDK 10.0.101 present), C#, **WPF** | Boring, native, dark theme is just brushes, single-file publish |
| Audio | **NAudio.Wasapi** NuGet | Mature Core Audio wrappers (`MMDeviceEnumerator`, `AudioSessionManager`, `AudioSessionControl` with meter + volume). Only dependency. |
| Tray | `System.Windows.Forms.NotifyIcon` (`UseWindowsForms=true`) | In the box, no package |
| App icons | `System.Drawing.Icon.ExtractAssociatedIcon` | In the box |
| Config | `System.Text.Json` | In the box |
| Dark title bar | one `DwmSetWindowAttribute` P/Invoke | In the box |
| Build | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` | One exe, no runtime install (~150 MB: WPF + WinForms can't be trimmed). Framework-dependent is ~1 MB if preferred. |

Threading: Core Audio objects live on one dedicated engine thread (20 ms loop). The UI
reads an immutable snapshot (levels, state, target volume) on a 33 ms `DispatcherTimer`.
UI → engine is a config swap. No locks on COM objects.

## Known limitations

- Exclusive-mode audio apps bypass the session volume (rare; some audiophile players).
- If the target switches output device mid-session, a new session appears and is picked
  up on the next 2 s rescan.
- Elevated apps: can't read the exe path, so no icon (generic glyph).
- Volume interpolation is linear in mixer units, not dB. Curves give enough control;
  revisit if ramps feel wrong.
