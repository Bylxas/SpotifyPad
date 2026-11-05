namespace SpotifyPad;

/// <summary>
/// Background update checks and the "Update" action, shared by the tray menu and the settings window.
/// All state changes and <see cref="Changed"/> happen on the UI thread.
/// </summary>
sealed class UpdateManager : IDisposable
{
    static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(15);
    static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    readonly SynchronizationContext ui;
    readonly Func<AppConfig> config;
    readonly NotifyIcon tray;
    readonly Action exitApp;
    readonly System.Threading.Timer timer;
    string? notifiedTag;

    public Updater.Release? Available { get; private set; }
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public event Action? Changed;

    /// <param name="exitApp">Closes the app after the new version was started.</param>
    public UpdateManager(SynchronizationContext ui, Func<AppConfig> config, NotifyIcon tray, Action exitApp)
    {
        this.ui = ui;
        this.config = config;
        this.tray = tray;
        this.exitApp = exitApp;
        timer = new System.Threading.Timer(_ => ui.Post(_ => _ = CheckAsync(manual: false), null), null, FirstCheck, Interval);
    }

    /// <param name="manual">"Check now" in the settings: runs even when automatic checks are off.</param>
    public async Task CheckAsync(bool manual)
    {
        if (Busy || (!manual && !config().CheckForUpdates)) return;
        if (manual) SetStatus(T("Suche nach Updates…", "Checking for updates…"));
        try
        {
            var release = await Updater.CheckAsync(Updater.Current);
            Available = release;
            SetStatus(release == null
                ? T($"Du hast die neueste Version (v{Updater.Current.ToString(3)}).", $"You have the latest version (v{Updater.Current.ToString(3)}).")
                : T($"Version {release.Tag} ist verfügbar.", $"Version {release.Tag} is available."));

            // One notification per new version and run
            if (release != null && release.Tag != notifiedTag)
            {
                notifiedTag = release.Tag;
                tray.ShowBalloonTip(8000, T("SpotifyPad-Update verfügbar", "SpotifyPad update available"),
                    T($"Version {release.Tag} ist da. Klick hier und dann auf „Update“.", $"Version {release.Tag} is out. Click here, then \"Update\"."),
                    ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            SetStatus(T("Update-Prüfung fehlgeschlagen: ", "Update check failed: ") + ex.Message);
        }
    }

    /// <summary>Downloads and installs <see cref="Available"/>, then restarts into the new version.</summary>
    public async void Install(IWin32Window? owner = null)
    {
        if (Available is not { } release || Busy) return;
        Busy = true;
        var progress = new Progress<int>(p => SetStatus(T($"Update wird geladen… {p} %", $"Downloading update… {p} %")));
        try
        {
            string exe = Environment.ProcessPath!;
            await Updater.DownloadAndSwapAsync(release, exe, progress);
            SetStatus(T("Update installiert – SpotifyPad startet neu…", "Update installed – restarting SpotifyPad…"));
            // Free the single-instance lock first, so the new version can take over right away
            Program.ReleaseSingleInstance();
            Updater.StartNewVersion(exe);
            exitApp();
        }
        catch (Exception ex)
        {
            Busy = false;
            SetStatus(T("Update fehlgeschlagen: ", "Update failed: ") + ex.Message);
            var answer = MessageBox.Show(owner,
                T($"Das Update konnte nicht installiert werden:\n{ex.Message}\n\nRelease-Seite im Browser öffnen, um es von Hand zu laden?",
                  $"The update couldn't be installed:\n{ex.Message}\n\nOpen the release page in your browser to download it manually?"),
                "SpotifyPad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) Updater.OpenReleasePage(release.PageUrl);
        }
    }

    void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose() => timer.Dispose();
}
