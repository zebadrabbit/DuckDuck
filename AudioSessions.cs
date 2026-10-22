using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DuckDuck;

/// Core Audio sessions on every active output device, grouped by process name.
/// Use from one thread only (the engine thread).
public sealed class AudioSessions : IDisposable
{
    public sealed record AppInfo(string Name, float Peak, string? ExePath);

    sealed class App { public readonly List<AudioSessionControl> Sessions = new(); public string? Exe; }

    readonly MMDeviceEnumerator enumerator = new();
    readonly Dictionary<string, MMDevice> devices = new();
    readonly Dictionary<string, App> apps = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<uint, (string Name, string? Exe)> procs = new();

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
                if (pid == 0 || s.State == AudioSessionState.AudioSessionStateExpired || !Resolve(pid, out var p)) { s.Dispose(); continue; }
                livePids.Add(pid);
                if (!apps.TryGetValue(p.Name, out var app)) apps[p.Name] = app = new App { Exe = p.Exe };
                app.Sessions.Add(s);
                app.Exe ??= p.Exe;
            }
        }
        foreach (var pid in procs.Keys.Where(k => !livePids.Contains(k)).ToList()) procs.Remove(pid);
    }

    bool Resolve(uint pid, out (string Name, string? Exe) p)
    {
        if (procs.TryGetValue(pid, out p)) return true;
        try
        {
            using var pr = Process.GetProcessById((int)pid);
            string? exe = null;
            try { exe = pr.MainModule?.FileName; } catch { /* elevated or protected process: no icon */ }
            procs[pid] = p = (pr.ProcessName, exe);
            return true;
        }
        catch { p = default; return false; }   // process already gone
    }

    public IEnumerable<string> Names => apps.Keys;

    public float Peak(string name)
    {
        if (!apps.TryGetValue(name, out var app)) return 0;
        float peak = 0;
        foreach (var s in app.Sessions) peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue);
        return peak;
    }

    /// null when the app has no session (not running / never played).
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
        foreach (var (n, a) in apps) d[n] = new AppInfo(n, Peak(n), a.Exe);
        return d;
    }

    public void Dispose()
    {
        foreach (var a in apps.Values) foreach (var s in a.Sessions) s.Dispose();
        foreach (var d in devices.Values) d.Dispose();
        enumerator.Dispose();
    }
}
