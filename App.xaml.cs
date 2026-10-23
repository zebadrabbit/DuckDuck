using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DuckDuck;

public partial class App : Application
{
    public static Config Config = null!;
    public static Engine Engine = null!;
    public static bool Quitting;

    Forms.NotifyIcon? tray;
    Mutex? single;
    EventWaitHandle? wake;

    [DllImport("kernel32")] static extern bool AttachConsole(int pid);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest")) { AttachConsole(-1); Console.WriteLine(); Mta(CaptureMeter.SelfTest); Ducker.SelfTest(); Shutdown(0); return; }
        if (e.Args.Contains("--list")) { AttachConsole(-1); Console.WriteLine(); ListApps(); Shutdown(0); return; }

        single = new Mutex(true, "DuckDuck.SingleInstance", out bool first);
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, "DuckDuck.Show");
        if (!first) { wake.Set(); Shutdown(0); return; }   // already running (maybe in the tray): poke it to show its window
        new Thread(() => { while (wake.WaitOne()) try { Dispatcher.Invoke(ShowMain); } catch { } }) { IsBackground = true, Name = "DuckDuck wake" }.Start();

        Config = Config.Load();
        Engine = new Engine(Config);
        Engine.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Engine.Stop();   // put the target back before we die

        // tray: left-click shows, right-click → Enabled / Show / Exit
        var menu = new Forms.ContextMenuStrip();
        var enabled = new Forms.ToolStripMenuItem("Enabled") { CheckOnClick = true };
        enabled.Click += (_, _) => { Apply(Config with { Enabled = enabled.Checked }); Config.Save(); (MainWindow as MainWindow)?.SyncFromConfig(); };
        menu.Opening += (_, _) => enabled.Checked = Config.Enabled;
        menu.Items.Add(enabled);
        menu.Items.Add("Show", null, (_, _) => ShowMain());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { Quitting = true; Shutdown(); });
        tray = new Forms.NotifyIcon
        {
            Visible = true,
            Icon = new System.Drawing.Icon(GetResourceStream(new Uri("pack://application:,,,/duck.ico"))!.Stream),
            ContextMenuStrip = menu,
        };
        tray.MouseClick += (_, me) => { if (me.Button == Forms.MouseButtons.Left) ShowMain(); };
        Apply(Config);
        if (AutoStart.Enabled) AutoStart.Set(true);   // re-point the Run entry at wherever this exe lives now

        MainWindow = new MainWindow();
        if (!Config.StartMinimized && !e.Args.Contains("--tray")) MainWindow.Show();   // --tray: launched by Windows at logon, stay out of the way
    }

    /// The one place a new Config goes live: engine, tray tooltip. Saving is the caller's job.
    public static void Apply(Config c)
    {
        Config = c;
        Engine.Apply(c);
        if (Current is App { tray: { } t }) t.Text = c.Enabled ? "DuckDuck" : "DuckDuck (off)";
    }

    void ShowMain()
    {
        MainWindow!.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        Engine?.Stop();
        base.OnExit(e);
    }

    /// Run-at-logon via HKCU\...\Run. The registry holds the state; nothing in config.json.
    public static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string Command => $"\"{Environment.ProcessPath}\" --tray";

        public static bool Enabled
        {
            get { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue("DuckDuck") != null; }
        }

        public static void Set(bool on)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            if (on) k.SetValue("DuckDuck", Command);
            else k.DeleteValue("DuckDuck", throwOnMissingValue: false);
        }
    }

    /// `DuckDuck.exe --list`: dump audio apps + peaks for 5 s. The M2 go/no-go check.
    /// `*` after a peak: session-meter fallback, process loopback unavailable for that app.
    static void ListApps() => Mta(() =>
    {
        using var audio = new AudioSessions();
        for (int i = 0; i < 10; i++)
        {
            audio.Rescan();
            var line = audio.Apps().Values.OrderBy(a => a.Name)
                .Select(a => $"{a.Name} peak={a.Peak:0.00}{(a.Metered ? "" : "*")} vol={audio.GetVolume(a.Name)?.ToString("0.00") ?? "-"}");
            Console.WriteLine(string.Join("   ", line));
            Thread.Sleep(500);
        }
    });

    /// Run on an MTA thread like the engine: process-loopback activation calls back from COM
    /// and would deadlock a blocked STA (the WPF main thread).
    static void Mta(ThreadStart f)
    {
        var t = new Thread(f);
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
    }
}
