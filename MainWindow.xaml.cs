using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DuckDuck;

public partial class MainWindow : Window
{
    const double MeterWidth = 90, VolWidth = 150;

    bool loading = true;
    readonly DispatcherTimer ui = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, (FrameworkElement Row, Rectangle Meter)> sourceRows = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Rectangle> pickerMeters = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ImageSource?> icons = new(StringComparer.OrdinalIgnoreCase);
    FrameworkElement? targetBlock;
    Rectangle? targetVolBar, targetRestBar;
    TextBlock? targetVolText;

    static Config Cfg => App.Config;
    Brush B(string key) => (Brush)FindResource(key);
    Style S(string key) => (Style)FindResource(key);
    FontFamily Glyph => (FontFamily)FindResource("Glyph");

    public MainWindow()
    {
        InitializeComponent();
        var c = Cfg;
        AttackCurve.ItemsSource = Enum.GetValues<Curve>();
        ReleaseCurve.ItemsSource = Enum.GetValues<Curve>();
        DuckTo.Value = c.DuckTo * 100;
        Attack.Value = c.AttackMs;
        Release.Value = c.ReleaseMs;
        Hold.Value = c.HoldMs;
        AttackCurve.SelectedItem = c.AttackCurve;
        ReleaseCurve.SelectedItem = c.ReleaseCurve;
        EnabledBox.IsChecked = c.Enabled;
        StartMin.IsChecked = c.StartMinimized;
        RunAtStartup.IsChecked = App.AutoStart.Enabled;   // the registry is the truth for this one, not config.json
        loading = false;

        RebuildSources();
        RebuildTarget();
        save.Tick += (_, _) => { save.Stop(); Cfg.Save(); };
        ui.Tick += (_, _) => Refresh();
        ui.Start();
        Picker.Closed += (_, _) => pickerMeters.Clear();
    }

    // ---- config plumbing ------------------------------------------------------------

    void Update(Config c)
    {
        App.Apply(c);
        save.Stop(); save.Start();   // debounce disk writes while a slider drags
    }

    /// The tray menu changed something: reflect it without re-saving.
    public void SyncFromConfig()
    {
        loading = true;
        EnabledBox.IsChecked = Cfg.Enabled;
        loading = false;
    }

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading) App.AutoStart.Set(RunAtStartup.IsChecked == true);
    }

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        Update(Cfg with
        {
            DuckTo = (float)(DuckTo.Value / 100),
            AttackMs = (int)Attack.Value,
            AttackCurve = AttackCurve.SelectedItem as Curve? ?? Cfg.AttackCurve,
            ReleaseMs = (int)Release.Value,
            ReleaseCurve = ReleaseCurve.SelectedItem as Curve? ?? Cfg.ReleaseCurve,
            HoldMs = (int)Hold.Value,
            Enabled = EnabledBox.IsChecked == true,
            StartMinimized = StartMin.IsChecked == true,
        });
    }

    // ---- source / target panels -------------------------------------------------------

    void RebuildSources()
    {
        SourceList.Children.Clear();
        sourceRows.Clear();
        foreach (var name in Cfg.Sources)
        {
            var meter = Fill(B("Amber"));
            var marker = new Rectangle { Fill = B("Text"), Width = 2, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left };
            var row = AppRow(name, ExeFor(name), meter, () =>
            {
                Update(Cfg with { Sources = Cfg.Sources.Where(s => !s.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray() });
                RebuildSources();
            }, marker);

            // per-source trigger threshold; the marker sits on this source's meter so you can watch the peak cross it
            var slider = new Slider { Minimum = 0, Maximum = 50, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 120, VerticalAlignment = VerticalAlignment.Center, Value = Cfg.ThresholdFor(name) * 100 };
            AutomationProperties.SetAutomationId(slider, "Threshold:" + name);
            var val = new TextBlock { Style = S("Readout"), Foreground = B("Muted"), MinWidth = 36, Margin = new Thickness(8, 0, 0, 0) };
            void Show(double pct) { val.Text = $"{pct:0} %"; marker.Margin = new Thickness(MeterWidth * Level((float)(pct / 100)), 0, 0, 0); }
            Show(slider.Value);
            slider.ValueChanged += (_, e) =>
            {
                Show(e.NewValue);
                Update(Cfg with { SourceThresholds = new Dictionary<string, float>(Cfg.SourceThresholds) { [name] = (float)(e.NewValue / 100) } });
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, -4, 0, 8) };
            line.Children.Add(new TextBlock { Text = "Trigger at", Style = S("Label"), Margin = new Thickness(0, 0, 10, 0) });
            line.Children.Add(slider);
            line.Children.Add(val);

            var block = new StackPanel();
            block.Children.Add(row);
            block.Children.Add(line);
            SourceList.Children.Add(block);
            sourceRows[name] = (block, meter);
        }
        if (Cfg.Sources.Length == 0) SourceList.Children.Add(Hint("No sources yet. Add the apps that should duck the target."));
    }

    void RebuildTarget()
    {
        TargetBox.Children.Clear();
        targetBlock = null; targetVolBar = null; targetRestBar = null; targetVolText = null;
        if (Cfg.Target is not string t)
        {
            TargetBox.Children.Add(Hint("No target yet. Pick the app to duck."));
            ChangeTargetText.Text = "pick app";
            return;
        }
        ChangeTargetText.Text = "change";

        var block = new StackPanel();
        block.Children.Add(AppRow(t, ExeFor(t), null, null));
        targetVolBar = Fill(B("Blue"));
        targetRestBar = Fill(B("BlueDim"));   // ghost: where the volume will return to
        targetVolText = new TextBlock { Style = S("Readout"), Foreground = B("Blue"), Margin = new Thickness(10, 0, 0, 0), MinWidth = 40 };
        var volRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 2, 0, 0) };
        volRow.Children.Add(new TextBlock { Text = "Volume", Style = S("Label"), Margin = new Thickness(0, 0, 10, 0) });
        volRow.Children.Add(Track(targetVolBar, VolWidth, ghost: targetRestBar));
        volRow.Children.Add(targetVolText);
        block.Children.Add(volRow);
        TargetBox.Children.Add(block);
        targetBlock = block;
    }

    /// icon | name | [meter] | [×]
    FrameworkElement AppRow(string name, string? exe, Rectangle? meter, Action? remove, Rectangle? marker = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (remove != null)
        {
            var x = new Button { Style = S("Flat"), ToolTip = "Remove", Content = new TextBlock { Text = "", FontFamily = Glyph, FontSize = 11 } };
            x.Click += (_, _) => remove();
            DockPanel.SetDock(x, Dock.Right);
            row.Children.Add(x);
        }
        if (meter != null)
        {
            var t = Track(meter, MeterWidth, marker);
            t.Margin = new Thickness(10, 0, 10, 0);
            DockPanel.SetDock(t, Dock.Right);
            row.Children.Add(t);
        }
        var img = IconFor(exe);
        FrameworkElement icon = img != null
            ? new Image { Source = img, Width = 20, Height = 20 }
            : new TextBlock { Text = "", FontFamily = Glyph, FontSize = 18, Foreground = B("Muted") };
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = Pretty(name), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    // meters are pills: 8 px tall, 4 px radius, on a track trough
    static Rectangle Fill(Brush b) => new() { Fill = b, Width = 0, HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 4, RadiusY = 4 };

    Border Track(Rectangle fill, double width, Rectangle? marker = null, Rectangle? ghost = null)
    {
        var g = new Grid();
        if (ghost != null) g.Children.Add(ghost);
        g.Children.Add(fill);
        if (marker != null) g.Children.Add(marker);
        return new Border
        {
            Width = width, Height = 8, CornerRadius = new CornerRadius(4), Background = B("Track"),
            VerticalAlignment = VerticalAlignment.Center, Child = g,
        };
    }

    TextBlock Hint(string text) => new() { Text = text, Style = S("Hint"), Margin = new Thickness(0, 4, 0, 8) };

    static string Pretty(string name) => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    static string? ExeFor(string name) => App.Engine.Snapshot.Apps.TryGetValue(name, out var a) ? a.ExePath : null;

    ImageSource? IconFor(string? exe)
    {
        if (exe == null) return null;
        if (icons.TryGetValue(exe, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (ico != null)
            {
                img = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
            }
        }
        catch { /* no icon: generic glyph */ }
        return icons[exe] = img;
    }

    // ---- live refresh (33 ms) ---------------------------------------------------------

    void Refresh()
    {
        var s = App.Engine.Snapshot;
        var c = Cfg;

        foreach (var (name, (row, meter)) in sourceRows)
        {
            bool running = s.Apps.TryGetValue(name, out var a);
            row.Opacity = running ? 1 : 0.45;
            meter.Width = MeterWidth * Level(running ? a!.Peak : 0);
            if (running && a!.ExePath != null && !icons.ContainsKey(a.ExePath)) { RebuildSources(); return; }   // app appeared: fetch its icon once
        }
        foreach (var (name, m) in pickerMeters)
            m.Width = MeterWidth * Level(s.Apps.TryGetValue(name, out var a) ? a.Peak : 0);

        bool ducking = s.State != DuckState.Idle;
        bool targetUp = c.Target != null && s.TargetVolume != null;
        TargetBorder.BorderBrush = B(ducking ? "Blue" : "BlueDim");
        if (targetBlock != null)
        {
            targetBlock.Opacity = targetUp ? 1 : 0.45;
            targetVolBar!.Width = VolWidth * (s.TargetVolume ?? 0);
            targetRestBar!.Width = VolWidth * (s.RestVolume ?? 0);
            targetVolText!.Text = s.TargetVolume is float v ? $"{v * 100:0} %" : "—";
        }
        if (c.Target != null && s.Apps.TryGetValue(c.Target, out var ta) && ta.ExePath != null && !icons.ContainsKey(ta.ExePath)) RebuildTarget();

        var (word, brush) =
            !c.Enabled ? ("Off", "Muted")
            : !targetUp ? ("Idle", "Muted")
            : ducking ? ("Ducking", "Blue")
            : ("Listening", "Muted");
        StatusText.Text = word;
        StatusText.Foreground = StatusDot.Fill = B(brush);
        StatusReadout.Text = c.Enabled && ducking && targetUp ? $"{(s.TargetVolume ?? 0) * 100:0} %" : "";
    }

    /// Peak (linear amplitude) → bar fraction, -50 dB..0 dB. Linear looks dead for speech.
    static double Level(float peak) => peak <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(peak) / 50, 0, 1);

    // ---- picker -----------------------------------------------------------------------

    void AddSource_Click(object sender, RoutedEventArgs e) => OpenPicker(AddSource, forTarget: false);
    void ChangeTarget_Click(object sender, RoutedEventArgs e) => OpenPicker(ChangeTarget, forTarget: true);

    void OpenPicker(UIElement anchor, bool forTarget)
    {
        PickerList.Children.Clear();
        pickerMeters.Clear();
        var chosen = new HashSet<string>(Cfg.Sources, StringComparer.OrdinalIgnoreCase);
        if (Cfg.Target != null) chosen.Add(Cfg.Target);

        foreach (var a in App.Engine.Snapshot.Apps.Values.OrderBy(a => a.Name))
        {
            if (chosen.Contains(a.Name)) continue;
            var name = a.Name;
            var meter = Fill(B(forTarget ? "Blue" : "Amber"));
            var btn = new Button
            {
                Style = S("Flat"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = AppRow(name, a.ExePath, meter, null),
            };
            btn.Click += (_, _) =>
            {
                Picker.IsOpen = false;
                if (forTarget) { Update(Cfg with { Target = name }); RebuildTarget(); }
                else { Update(Cfg with { Sources = [.. Cfg.Sources, name] }); RebuildSources(); }
            };
            PickerList.Children.Add(btn);
            pickerMeters[name] = meter;
        }
        if (PickerList.Children.Count == 0) PickerList.Children.Add(Hint("No other apps have audio right now."));
        Picker.PlacementTarget = anchor;
        Picker.IsOpen = true;
    }

    // ---- close → tray -----------------------------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Quitting) return;
        e.Cancel = true;
        Hide();
    }
}
