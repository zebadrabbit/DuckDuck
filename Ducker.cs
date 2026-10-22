namespace DuckDuck;

public enum DuckState { Idle, Attack, Ducked, Release }

/// The ducking envelope. Pure: no COM, no threads, no clock. Step() returns the volume to
/// write to the target, or null while Idle (engine must not touch the slider then).
public sealed class Ducker
{
    public DuckState State { get; private set; }
    public float Rest { get; private set; }   // target's un-ducked volume, sampled every Idle tick

    float from, cur, elapsed, quietMs;

    public void Reset() => State = DuckState.Idle;

    /// active: any source is above its threshold this tick (engine decides).
    public float? Step(Config c, float dtMs, bool active, float currentVol)
    {
        switch (State)
        {
            case DuckState.Idle:
                Rest = cur = currentVol;
                return active ? Begin(DuckState.Attack) : null;

            case DuckState.Attack:   // always finishes, even if the source went quiet
                elapsed += dtMs;
                float ta = c.AttackMs <= 0 ? 1 : Math.Min(1, elapsed / c.AttackMs);
                cur = Lerp(from, DuckVol(c), Ease(c.AttackCurve, ta));
                if (ta >= 1) { State = DuckState.Ducked; quietMs = 0; }
                return cur;

            case DuckState.Ducked:
                quietMs = active ? 0 : quietMs + dtMs;
                if (quietMs >= c.HoldMs) return Begin(DuckState.Release);
                cur = DuckVol(c);   // ponytail: while ducked we own the slider, so live DuckTo tweaks apply at once
                return cur;

            case DuckState.Release:
                if (active) return Begin(DuckState.Attack);   // retrigger from wherever we are, no jump
                elapsed += dtMs;
                float tr = c.ReleaseMs <= 0 ? 1 : Math.Min(1, elapsed / c.ReleaseMs);
                cur = Lerp(from, Rest, Ease(c.ReleaseCurve, tr));
                if (tr >= 1) State = DuckState.Idle;
                return cur;
        }
        return null;
    }

    float DuckVol(Config c) => Math.Min(Rest, c.DuckTo);   // never raise

    float Begin(DuckState s)
    {
        State = s; from = cur; elapsed = 0; quietMs = 0;
        return cur;
    }

    static float Lerp(float a, float b, float p) => a + (b - a) * p;

    public static float Ease(Curve c, float t) => c switch
    {
        Curve.Fast => 1 - (1 - t) * (1 - t),
        Curve.Slow => t * t,
        Curve.Smooth => t * t * (3 - 2 * t),
        _ => t,
    };

    // ---- `DuckDuck.exe --selftest` ----------------------------------------------------

    public static void SelfTest()
    {
        var c = new Config { DuckTo = 0.2f, AttackMs = 100, AttackCurve = Curve.Linear, ReleaseMs = 200, ReleaseCurve = Curve.Linear, HoldMs = 100 };
        var d = new Ducker();

        Check(d.Step(c, 20, false, 0.8f) == null && d.State == DuckState.Idle, "idle while quiet writes nothing");
        Check(d.Step(c, 20, true, 0.8f) == 0.8f && d.State == DuckState.Attack, "trigger starts attack at current volume");
        Check(Near(d.Step(c, 20, true, 0.8f), 0.68f), "attack ramps linearly");                 // t = 0.2
        for (int i = 0; i < 4; i++) d.Step(c, 20, false, 0.5f);                                 // quiet during attack: still finishes
        Check(d.State == DuckState.Ducked && Near(d.Step(c, 20, true, 0.2f), 0.2f), "reaches duckTo after attackMs");
        for (int i = 0; i < 4; i++) d.Step(c, 20, false, 0.2f);                                 // 80 ms quiet
        Check(d.State == DuckState.Ducked, "hold keeps it ducked");
        Check(d.Step(c, 20, false, 0.2f) == 0.2f && d.State == DuckState.Release, "quiet >= hold starts release");  // 100 ms
        Check(Near(d.Step(c, 20, false, 0.2f), 0.26f), "release ramps toward rest");            // t = 0.1
        Check(Near(d.Step(c, 20, true, 0.26f), 0.26f) && d.State == DuckState.Attack, "retrigger attacks from current volume");
        for (int i = 0; i < 6; i++) d.Step(c, 20, true, 0.2f);
        for (int i = 0; i < 6; i++) d.Step(c, 20, false, 0.2f);
        Check(d.State == DuckState.Release, "second cycle releases");
        for (int i = 0; i < 11; i++) d.Step(c, 20, false, 0.8f);
        Check(d.State == DuckState.Idle, "release ends idle");

        var low = new Ducker();
        low.Step(c, 20, false, 0.1f);                                                            // rest 0.1 < duckTo 0.2
        for (int i = 0; i < 8; i++) Check((low.Step(c, 20, true, 0.1f) ?? 0) <= 0.1f + 1e-6f, "never raises above rest");

        var t = new Config { Sources = ["a", "b"], SourceThresholds = { ["b"] = 0.15f } };
        Check(t.ThresholdFor("a") == Config.DefaultThreshold && t.ThresholdFor("b") == 0.15f, "per-source threshold with default fallback");

        Check(Ease(Curve.Fast, 0) == 0 && Ease(Curve.Fast, 1) == 1 && Near(Ease(Curve.Smooth, 0.5f), 0.5f), "curve endpoints");

        Console.WriteLine("selftest OK");
    }

    static bool Near(float? v, float x) => v is float f && MathF.Abs(f - x) < 1e-4f;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        Console.Error.WriteLine("FAIL: " + what);
        Environment.Exit(1);
    }
}
