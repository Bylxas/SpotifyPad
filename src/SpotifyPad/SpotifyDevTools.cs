using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SpotifyPad;

/// <summary>
/// Changes Spotify's own volume slider through Spotify's built-in Chromium DevTools endpoint.
/// Runs JavaScript inside the Spotify page, so the Spotify window is never activated.
/// Requires Spotify to be started with <see cref="Flag"/>.
/// </summary>
static class SpotifyDevTools
{
    public const int Port = 9333;
    public const string Flag = "--remote-debugging-port=9333";

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(1) };
    static ClientWebSocket? socket;
    static int nextId;

    /// <summary>Cheap check whether Spotify was started with the DevTools port.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync("127.0.0.1", Port).Wait(300) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="deltaPercent">Percent to change, 0 = toggle mute.</param>
    /// <returns>false if Spotify is not reachable.</returns>
    public static bool TryAdjustVolume(int deltaPercent) => Evaluate(VolumeScript.Replace("DELTA", deltaPercent.ToString())) == "ok";

    // Reads the real volume from the progress bar (the range input snaps to 10 %), then sets the input
    // with step="any" so Spotify's React slider accepts fine values.
    const string VolumeScript = """
        (() => {
          const bar = document.querySelector('[data-testid="volume-bar"]');
          if (!bar) return 'missing';
          const delta = DELTA;
          const input = bar.querySelector('input[type=range]');
          if (!input) return 'missing';
          const progress = bar.querySelector('[data-testid="progress-bar"]');
          let current = progress ? parseFloat(progress.style.getPropertyValue('--progress-bar-transform')) / 100 : NaN;
          if (isNaN(current)) current = parseFloat(input.value);
          let next;
          if (delta === 0) {
            // Mute via the slider (Spotify's mute button is too slow for quick toggles)
            if (current > 0) { window.__spotifyPadLast = current; next = 0; }
            else next = window.__spotifyPadLast || 0.3;
          } else {
            next = Math.min(1, Math.max(0, Math.round(current * 100 + delta) / 100));
          }
          const step = input.step;
          input.step = 'any';
          Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, String(next));
          input.dispatchEvent(new Event('input', { bubbles: true }));
          input.dispatchEvent(new Event('change', { bubbles: true }));
          input.step = step;
          return 'ok';
        })()
        """;

    static string? Evaluate(string expression)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var s = Connect();
                if (s == null) return null;
                return Run(s, expression);
            }
            catch
            {
                // Spotify restarted or port closed: drop the socket and retry once
                socket?.Dispose();
                socket = null;
            }
        }
        return null;
    }

    static ClientWebSocket? Connect()
    {
        if (socket?.State == WebSocketState.Open) return socket;
        socket?.Dispose();
        socket = null;

        var json = http.GetStringAsync($"http://127.0.0.1:{Port}/json").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        foreach (var page in doc.RootElement.EnumerateArray())
        {
            if (page.GetProperty("type").GetString() != "page") continue;
            if (page.GetProperty("url").GetString()?.StartsWith("https://xpui.", StringComparison.Ordinal) != true) continue;

            var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(2000);
            ws.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), cts.Token).GetAwaiter().GetResult();
            return socket = ws;
        }
        return null;
    }

    static string? Run(ClientWebSocket s, string expression)
    {
        int id = ++nextId;
        var message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true },
        });

        using var cts = new CancellationTokenSource(2000);
        s.SendAsync(message, WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();

        var buffer = new byte[8192];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = s.ReceiveAsync(buffer, cts.Token).GetAwaiter().GetResult();
                if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed");
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            // Skip events and replies to other requests
            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id) continue;
            return root.TryGetProperty("result", out var res)
                && res.TryGetProperty("result", out var inner)
                && inner.TryGetProperty("value", out var value)
                ? value.ToString()
                : null;
        }
    }
}

/// <summary>Starts Spotify with the DevTools flag and keeps its autostart/shortcuts using it.</summary>
static class SpotifyLauncher
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string ExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>Closes Spotify and starts it again with the volume control flag.</summary>
    /// <param name="minimized">Start Spotify hidden in the tray.</param>
    public static void Restart(bool minimized = false)
    {
        // Check before killing, so a Store/other install isn't closed without a way to start it again
        if (!IsInstalled) throw new FileNotFoundException(T("Spotify wurde nicht gefunden: ", "Spotify was not found: ") + ExePath);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Spotify"))
        {
            try { p.Kill(); } catch { }
            p.Dispose();
        }
        Thread.Sleep(1500);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath, minimized ? "--minimized " + Flag : Flag) { UseShellExecute = false });
    }

    static string Flag => SpotifyDevTools.Flag;

    /// <summary>True if Spotify's Windows autostart entry already carries the flag.</summary>
    public static bool IsAutostartPatched
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Spotify") is string v && v.Contains(Flag);
        }
    }

    /// <summary>Adds or removes the flag on Spotify's autostart entry and its Start menu / desktop shortcuts.</summary>
    public static void Patch(bool enable)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            if (key?.GetValue("Spotify") is string v)
                key.SetValue("Spotify", WithFlag(v, enable));
        }

        string[] shortcuts =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Spotify.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Spotify.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Spotify.lnk"),
        ];
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (var path in shortcuts.Where(File.Exists))
        {
            dynamic link = shell.CreateShortcut(path);
            link.Arguments = WithFlag((string)link.Arguments, enable);
            link.Save();
        }
    }

    static string WithFlag(string args, bool enable)
    {
        args = args.Replace(Flag, "").Trim();
        while (args.Contains("  ")) args = args.Replace("  ", " ");
        return enable ? $"{args} {Flag}".Trim() : args;
    }
}
