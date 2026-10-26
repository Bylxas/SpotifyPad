using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpotifyPad;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "SpotifyPad_Show");
        using var mutex = new Mutex(true, "SpotifyPad_SingleInstance", out bool first);
        if (!first)
        {
            // Already running: ask that instance to open its settings window
            showEvent.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Autostart passes --tray to start silently; a manual start opens the settings
        Application.Run(new TrayApp(showEvent, openSettings: !args.Contains("--tray")));
    }

    /// <summary>Frees memory after UI was shown so the tray app stays tiny in the background.</summary>
    public static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}

sealed class TrayApp : ApplicationContext
{
    readonly Engine engine = new();
    readonly SpotifyWatcher watcher;
    readonly NotifyIcon tray;
    ToolStripMenuItem enabledItem = null!;
    AppConfig cfg = AppConfig.Load();
    SettingsForm? settings;

    public TrayApp(EventWaitHandle showEvent, bool openSettings)
    {
        Lang.Apply(cfg.Language);
        engine.Apply(cfg);
        engine.Start();
        watcher = new SpotifyWatcher(() => cfg);

        // Spotify updates can rewrite its shortcuts, so re-apply the launch flag on every start
        // Keep the autostart entry pointing at this exe; it can go missing (e.g. when it was set from a
        // sandboxed process whose registry writes Windows never sees) or go stale when the exe is moved
        if (cfg.StartWithWindows == null)
        {
            cfg.StartWithWindows = Autostart.IsEnabled;
            try { cfg.Save(); } catch { }
        }
        if (cfg.StartWithWindows == true)
        {
            try { Autostart.Set(true); } catch { }
        }

        if (cfg.PatchSpotifyLaunch)
        {
            try { SpotifyLauncher.Patch(true); } catch { }
        }

        tray = new NotifyIcon { ContextMenuStrip = BuildMenu(), Visible = true };
        tray.DoubleClick += (_, _) => OpenSettings();
        UpdateIcon();

        // The ContextMenuStrip installed the WinForms sync context for this thread
        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => OpenSettings(), null), null, -1, false);
        if (openSettings)
            ui.Post(_ => OpenSettings(), null);
        else
            Program.TrimMemory();
    }

    /// <summary>Builds the tray menu in the current language.</summary>
    ContextMenuStrip BuildMenu()
    {
        enabledItem = new ToolStripMenuItem(T("Aktiv", "Active"), null, (_, _) => ToggleEnabled()) { Checked = cfg.Enabled };
        var menu = new ContextMenuStrip();
        menu.Items.Add(enabledItem);
        var presetMenu = new ToolStripMenuItem("Preset");
        menu.Items.Add(presetMenu);
        // Rebuilt on every open so it reflects presets saved in the settings
        menu.Opening += (_, _) =>
        {
            presetMenu.DropDownItems.Clear();
            foreach (var p in cfg.Presets)
            {
                var name = p.Name;
                presetMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SwitchPreset(name)) { Checked = name == cfg.Active.Name });
            }
        };
        menu.Items.Add(T("Einstellungen / Tasten…", "Settings / buttons…"), null, (_, _) => OpenSettings());
        menu.Items.Add(T("Spotify mit Lautstärke-Steuerung neu starten", "Restart Spotify with volume control"), null, (_, _) => RestartSpotify());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(T("Beenden", "Exit"), null, (_, _) => ExitThread());
        return menu;
    }

    static void RestartSpotify()
    {
        try
        {
            SpotifyLauncher.Restart();
        }
        catch (Exception ex)
        {
            MessageBox.Show(T("Spotify konnte nicht neu gestartet werden: ", "Couldn't restart Spotify: ") + ex.Message, "SpotifyPad");
        }
    }

    void ToggleEnabled()
    {
        cfg.Enabled = !cfg.Enabled;
        cfg.Save();
        engine.Apply(cfg);
        UpdateIcon();
    }

    void SwitchPreset(string name)
    {
        cfg.ActivePreset = name;
        cfg.Save();
        engine.Apply(cfg);
        UpdateIcon();
    }

    void UpdateIcon()
    {
        enabledItem.Checked = cfg.Enabled;
        tray.Icon = cfg.Enabled ? TrayIcons.On : TrayIcons.Off;
        string text = (cfg.Enabled ? T("SpotifyPad – aktiv", "SpotifyPad – active") : T("SpotifyPad – pausiert", "SpotifyPad – paused")) + " · " + cfg.Active.Name;
        // NotifyIcon tooltips are limited to 63 characters
        tray.Text = text.Length > 63 ? text[..63] : text;
    }

    void OpenSettings()
    {
        if (settings != null)
        {
            settings.Activate();
            return;
        }

        settings = new SettingsForm(cfg, engine);
        settings.FormClosed += (_, _) =>
        {
            bool reopen = false;
            if (settings.Saved)
            {
                // A language change rebuilds the menu and shows the settings again in the new language
                reopen = settings.Config.Language != cfg.Language;
                cfg = settings.Config;
                cfg.Save();
                engine.Apply(cfg);
                if (reopen)
                {
                    Lang.Apply(cfg.Language);
                    var old = tray.ContextMenuStrip;
                    tray.ContextMenuStrip = BuildMenu();
                    old?.Dispose();
                }
                UpdateIcon();
            }
            settings.Dispose();
            settings = null;
            if (reopen) SynchronizationContext.Current?.Post(_ => OpenSettings(), null);
            else Program.TrimMemory();
        };
        settings.Show();
        settings.Activate();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        watcher.Dispose();
        engine.Dispose();
        base.ExitThreadCore();
    }
}

static class TrayIcons
{
    public static readonly Icon On = Make(Color.FromArgb(30, 185, 84));
    public static readonly Icon Off = Make(Color.FromArgb(120, 120, 120));

    static Icon Make(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI Symbol", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("♪", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SpotifyPad";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(Name, false);
    }
}
