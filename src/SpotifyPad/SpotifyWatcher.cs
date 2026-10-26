using System.Diagnostics;

namespace SpotifyPad;

/// <summary>
/// Restarts Spotify with the DevTools flag whenever it runs without it (e.g. after a Spotify self-update,
/// via a link, or when it was already running before SpotifyPad), so the volume slider control keeps working.
/// If music was playing, playback is resumed after the restart.
/// </summary>
sealed class SpotifyWatcher : IDisposable
{
    const int CheckMs = 3000;
    // Give Spotify time to open its DevTools port before deciding it's missing
    const int SettleMs = 4000;
    // Never restart more often than this, and give up after a few tries in case Spotify ignores the flag
    const int MinGapMs = 120000;
    const int MaxFailedRestarts = 2;
    // How long to wait for the restarted Spotify before resuming playback
    const int ResumeTimeoutMs = 30000;
    const int ResumeDelayMs = 2500;

    readonly Func<AppConfig> config;
    readonly System.Threading.Timer timer;
    int checkedPid;
    long lastRestart = -MinGapMs;
    int failedRestarts;
    int busy;

    public SpotifyWatcher(Func<AppConfig> config)
    {
        this.config = config;
        timer = new System.Threading.Timer(_ => Check(), null, CheckMs, CheckMs);
    }

    void Check()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        var procs = Array.Empty<Process>();
        try
        {
            var cfg = config();
            if (!cfg.AutoRestartSpotify || cfg.VolumeTarget != VolumeTarget.Spotify) return;

            procs = Process.GetProcessesByName("Spotify");
            if (procs.Length == 0)
            {
                checkedPid = 0;
                failedRestarts = 0;
                return;
            }

            // The main process is the first one started; helpers (GPU, renderer…) come after it
            var main = procs.MinBy(StartTime)!;
            if (main.Id == checkedPid) return;
            var age = DateTime.Now - StartTime(main);
            if (age.TotalMilliseconds < SettleMs) return;
            checkedPid = main.Id;

            if (SpotifyDevTools.IsAvailable())
            {
                failedRestarts = 0;
                return;
            }
            long now = Environment.TickCount64;
            if (failedRestarts >= MaxFailedRestarts || now - lastRestart < MinGapMs) return;
            lastRestart = now;
            failedRestarts++;

            // Keep Spotify in the tray if it was opened that way (e.g. "--minimized" from its own autostart)
            bool minimized = procs.All(p => p.MainWindowHandle == IntPtr.Zero);
            bool wasPlaying = SpotifyControl.IsPlaying();
            SpotifyLauncher.Restart(minimized);
            if (wasPlaying) ResumeWhenReady(cfg);
        }
        catch
        {
            // Process info can vanish while Spotify starts or exits; try again next tick
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
            busy = 0;
        }
    }

    static void ResumeWhenReady(AppConfig cfg)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ResumeTimeoutMs && !SpotifyDevTools.IsAvailable()) Thread.Sleep(500);
        // The port opens before the player has loaded the last track
        Thread.Sleep(ResumeDelayMs);
        if (!SpotifyControl.IsPlaying()) SpotifyControl.Execute(PadAction.PlayPause, cfg);
    }

    static DateTime StartTime(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    public void Dispose() => timer.Dispose();
}
