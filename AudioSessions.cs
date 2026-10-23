using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DuckDuck;

/// Core Audio sessions on every active output device, grouped by process name, plus the
/// default microphone as a pseudo-app. Use from one thread only (the engine thread, MTA).
public sealed class AudioSessions : IDisposable
{
    /// Metered: the level is what the app renders (process loopback) or the mic endpoint.
    /// false: session-meter fallback, which also counts the app's own loopback captures.
    public sealed record AppInfo(string Name, float Peak, string? ExePath, bool Metered = true);

    /// Pseudo-source: the default communications microphone's endpoint meter. Only moves
    /// while some app is recording from it (Discord in a call), and cannot be a target.
    public const string Mic = "Microphone";

    sealed class App { public readonly List<AudioSessionControl> Sessions = new(); public readonly List<Proc> Procs = new(); public string? Exe; }
    sealed class Proc { public string Name = ""; public string? Exe; public DateTime Started; public ProcessMeter? Meter; }

    static readonly string Self = Process.GetCurrentProcess().ProcessName;

    readonly MMDeviceEnumerator enumerator = new();
    readonly Dictionary<string, MMDevice> devices = new();
    readonly Dictionary<string, App> apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<uint, Proc> procs = new();
    MMDevice? mic;

    /// Set from a COM callback when any device gets a new session. Caller rescans and clears it.
    public volatile bool Dirty;

    public void Rescan()
    {
        Dirty = false;

        var seen = new HashSet<string>();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            seen.Add(dev.ID);
            if (devices.TryAdd(dev.ID, dev)) dev.AudioSessionManager.OnSessionCreated += (_, _) => Dirty = true;
            else dev.Dispose();
        }
        foreach (var id in devices.Keys.Where(k => !seen.Contains(k)).ToList()) { devices[id].Dispose(); devices.Remove(id); }

        try
        {
            var m = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (mic?.ID == m.ID) m.Dispose(); else { mic?.Dispose(); mic = m; }
        }
        catch { mic?.Dispose(); mic = null; }   // no microphone

        foreach (var a in apps.Values) foreach (var s in a.Sessions) s.Dispose();
        apps.Clear();
        var livePids = new HashSet<uint>();
        foreach (var dev in devices.Values)
        {
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var col = mgr.Sessions;
            for (int i = 0; i < col.Count; i++)
            {
                var s = col[i];
                uint pid = s.GetProcessID;
                if (pid == 0 || s.State == AudioSessionState.AudioSessionStateExpired || Resolve(pid) is not Proc p) { s.Dispose(); continue; }
                if (!apps.TryGetValue(p.Name, out var app)) apps[p.Name] = app = new App { Exe = p.Exe };
                app.Sessions.Add(s);
                if (livePids.Add(pid)) app.Procs.Add(p);   // one meter per process, however many devices it plays on
                app.Exe ??= p.Exe;
            }
        }
        foreach (var pid in procs.Keys.Where(k => !livePids.Contains(k)).ToList()) Drop(pid);
    }

    /// Cached per PID, but checked against the process start time: Windows hands a dead
    /// process's PID to the next one within seconds, and Chromium apps churn processes.
    /// null for a gone process and for any DuckDuck (our process-loopback taps register a
    /// session; `--list` runs beside the tray instance).
    Proc? Resolve(uint pid)
    {
        try
        {
            using var pr = Process.GetProcessById((int)pid);
            if (pr.ProcessName.Equals(Self, StringComparison.OrdinalIgnoreCase)) return null;
            var started = StartTime(pr);
            if (procs.TryGetValue(pid, out var p) && p.Started == started) return p;
            Drop(pid);
            string? exe = null;
            try { exe = pr.MainModule?.FileName; } catch { /* elevated or protected process: no icon */ }
            return procs[pid] = new Proc { Name = pr.ProcessName, Exe = exe, Started = started, Meter = ProcessMeter.TryCreate(pid) };
        }
        catch { return null; }   // process already gone
    }

    static DateTime StartTime(Process p) { try { return p.StartTime; } catch { return default; } }

    void Drop(uint pid) { if (procs.Remove(pid, out var p)) p.Meter?.Dispose(); }

    public IEnumerable<string> Names => apps.Keys;

    /// Max over the app's processes of what they actually render (process loopback). Session
    /// meters only when no process could be metered: they also count an app's own loopback
    /// captures, which is how Discord's meter came to mirror everything else playing.
    /// Drains the meters, so read once per tick (Apps() does).
    public float Peak(string name)
    {
        if (!apps.TryGetValue(name, out var app)) return 0;
        float peak = 0; bool metered = false;
        foreach (var p in app.Procs) if (p.Meter is { } m) { metered = true; peak = Math.Max(peak, m.Peak()); }
        if (!metered) foreach (var s in app.Sessions) peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue);
        return peak;
    }

    /// null when the app has no session (not running / never played), and for the mic.
    public float? GetVolume(string? name) =>
        name != null && apps.TryGetValue(name, out var app) && app.Sessions.Count > 0
            ? app.Sessions[0].SimpleAudioVolume.Volume
            : null;

    public void SetVolume(string name, float v)
    {
        if (!apps.TryGetValue(name, out var app)) return;
        v = Math.Clamp(v, 0, 1);
        foreach (var s in app.Sessions) s.SimpleAudioVolume.Volume = v;
    }

    public Dictionary<string, AppInfo> Apps()
    {
        var d = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, a) in apps) d[n] = new AppInfo(n, Peak(n), a.Exe, a.Procs.Any(p => p.Meter != null));
        if (mic != null) d[Mic] = new AppInfo(Mic, mic.AudioMeterInformation.MasterPeakValue, null);
        return d;
    }

    public void Dispose()
    {
        foreach (var a in apps.Values) foreach (var s in a.Sessions) s.Dispose();
        foreach (var p in procs.Values) p.Meter?.Dispose();
        foreach (var d in devices.Values) d.Dispose();
        mic?.Dispose();
        enumerator.Dispose();
    }
}
