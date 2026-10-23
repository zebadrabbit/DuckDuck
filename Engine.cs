using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DuckDuck;

public sealed record Snapshot(IReadOnlyDictionary<string, AudioSessions.AppInfo> Apps, DuckState State, float? TargetVolume, float? RestVolume)
{
    public static readonly Snapshot Empty = new(new Dictionary<string, AudioSessions.AppInfo>(), DuckState.Idle, null, null);
}

/// Owns the Core Audio objects on one MTA thread, 20 ms loop.
/// UI swaps a Config in with Apply() and reads the latest Snapshot out. No shared mutable state.
public sealed class Engine
{
    // Exists while the target is ducked and holds its rest volume. If we die mid-duck, the
    // next launch (or the target's next appearance) puts the slider back.
    static readonly string Marker = Path.Combine(Config.Dir, "ducking");

    volatile Config cfg;
    volatile Snapshot snap = Snapshot.Empty;
    volatile bool running = true;
    readonly Thread thread;
    readonly Ducker ducker = new();

    public Engine(Config c)
    {
        cfg = c;
        thread = new Thread(Loop) { IsBackground = true, Name = "DuckDuck engine" };
        thread.SetApartmentState(ApartmentState.MTA);
    }

    public Snapshot Snapshot => snap;
    public void Apply(Config c) => cfg = c;
    public void Start() => thread.Start();
    public void Stop() { running = false; if (thread.IsAlive) thread.Join(3000); }

    void Loop()
    {
        using var audio = new AudioSessions();
        float? pendingRestore = ReadMarker();
        string? target = cfg.Target;   // not null: the first tick must not look like a target swap, or it would discard pendingRestore
        var sw = Stopwatch.StartNew();
        double last = 0, nextScan = 0;

        while (running)
        {
            try
            {
                double now = sw.Elapsed.TotalMilliseconds;
                float dt = (float)(now - last);
                last = now;
                audio.MicWanted = cfg.Sources.Contains(AudioSessions.Mic, StringComparer.OrdinalIgnoreCase);   // a change marks Dirty
                if (now >= nextScan || audio.Dirty) { audio.Rescan(); nextScan = now + 2000; }

                var c = cfg;
                if (c.Target != target)   // target swapped in the UI: un-duck the old one first
                {
                    if (target != null && ducker.State != DuckState.Idle) audio.SetVolume(target, ducker.Rest);
                    ducker.Reset();
                    pendingRestore = null;
                    File.Delete(Marker);
                    target = c.Target;
                }

                var apps = audio.Apps();   // once per tick: reading a process meter drains it
                bool active = false;
                if (c.Enabled) foreach (var s in c.Sources) if (apps.GetValueOrDefault(s)?.Peak >= c.ThresholdFor(s)) active = true;

                float? vol = audio.GetVolume(target);
                if (vol is float v)
                {
                    if (pendingRestore is float pr) { audio.SetVolume(target!, pr); v = pr; pendingRestore = null; File.Delete(Marker); }
                    var before = ducker.State;
                    float? w = ducker.Step(c, dt, active, v);
                    if (w is float wv) { audio.SetVolume(target!, wv); vol = wv; }
                    if (before == DuckState.Idle && ducker.State != DuckState.Idle) File.WriteAllText(Marker, ducker.Rest.ToString(CultureInfo.InvariantCulture));
                    else if (before != DuckState.Idle && ducker.State == DuckState.Idle) File.Delete(Marker);
                }
                else
                {
                    // Target vanished (closed) mid-duck: Windows remembers per-app volume, so put it
                    // back when the app shows up again. Marker is still on disk for the crash case.
                    if (ducker.State != DuckState.Idle) pendingRestore ??= ducker.Rest;
                    ducker.Reset();
                }

                snap = new Snapshot(apps, ducker.State, vol, vol == null ? null : ducker.Rest);
            }
            catch (Exception)
            {
                audio.Dirty = true;   // device yanked mid-tick etc. Rescan next tick.
            }
            Thread.Sleep(20);
        }

        try
        {
            if (target != null && ducker.State != DuckState.Idle) { audio.SetVolume(target, ducker.Rest); File.Delete(Marker); }
        }
        catch { }
    }

    static float? ReadMarker()
    {
        try
        {
            return File.Exists(Marker) && float.TryParse(File.ReadAllText(Marker), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
        }
        catch { return null; }
    }
}
