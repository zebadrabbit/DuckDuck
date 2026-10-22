using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuckDuck;

public enum Curve { Linear, Fast, Slow, Smooth }

/// Immutable. UI makes a new one with `with`, hands it to the engine, saves it.
public record Config
{
    public string[] Sources { get; init; } = [];
    public string? Target { get; init; }
    public float DuckTo { get; init; } = 0.20f;      // mixer units 0..1
    public int AttackMs { get; init; } = 150;
    public Curve AttackCurve { get; init; } = Curve.Fast;
    public int ReleaseMs { get; init; } = 800;
    public Curve ReleaseCurve { get; init; } = Curve.Smooth;
    public int HoldMs { get; init; } = 400;
    public Dictionary<string, float> SourceThresholds { get; init; } = new();   // peak 0..1 per source; missing → default
    public bool Enabled { get; init; } = true;   // off: never triggers; a duck in progress releases normally
    public bool StartMinimized { get; init; }

    public const float DefaultThreshold = 0.05f;   // idle browsers sit at ~0.01-0.02 peak, speech is well above
    public float ThresholdFor(string source) => SourceThresholds.TryGetValue(source, out var t) ? t : DefaultThreshold;

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DuckDuck");
    static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    static readonly object SaveLock = new();

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), Json) ?? new(); }
        catch { return new(); }   // missing or corrupt → defaults
    }

    public void Save()
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Dir);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }
}
