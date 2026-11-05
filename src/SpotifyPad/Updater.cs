using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpotifyPad;

/// <summary>Checks GitHub Releases for a newer SpotifyPad and swaps the running exe for it.</summary>
static class Updater
{
    const string Repo = "Bylxas/SpotifyPad";
    const string AssetName = "SpotifyPad.exe";
    const long MaxAssetBytes = 200L * 1024 * 1024;

    public sealed record Release(Version Version, string Tag, string PageUrl, string DownloadUrl, long Size, string? Sha256);

    static readonly HttpClient http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyPad-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The running version, e.g. 1.2.1 (the build metadata after '+' is ignored).</summary>
    public static Version Current { get; } = ParseVersion(Application.ProductVersion) ?? new Version(0, 0, 0);

    static Version? ParseVersion(string text)
    {
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var v) ? v : null;
    }

    /// <returns>The latest release if it is newer than <paramref name="current"/> and has the exe attached, else null.</returns>
    public static async Task<Release?> CheckAsync(Version current)
    {
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseVersion(tag) is not { } version || version <= current) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new Release(
                version,
                tag,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                digest is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? s[7..].ToLowerInvariant() : null);
        }
        return null;
    }

    /// <summary>
    /// Downloads the release next to <paramref name="exePath"/>, verifies it and puts it in place.
    /// The running exe can't be overwritten but can be renamed, so it is moved aside to "*.old.exe".
    /// </summary>
    public static async Task DownloadAndSwapAsync(Release release, string exePath, IProgress<int>? progress = null)
    {
        var uri = new Uri(release.DownloadUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith($"/{Repo}/releases/download/"))
            throw new InvalidOperationException(T("Unerwartete Download-Adresse.", "Unexpected download address."));
        if (release.Size <= 0 || release.Size > MaxAssetBytes)
            throw new InvalidOperationException(T("Unerwartete Dateigröße.", "Unexpected file size."));

        string dir = Path.GetDirectoryName(exePath)!;
        string download = exePath + ".download";
        string old = OldPath(exePath);

        try
        {
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read, lastPercent = -1;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > release.Size) throw new InvalidOperationException(T("Download ist größer als erwartet.", "Download is larger than expected."));
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    sha.AppendData(buffer, 0, read);
                    int percent = (int)(total * 100 / release.Size);
                    if (percent != lastPercent) progress?.Report(lastPercent = percent);
                }
                if (total != release.Size) throw new InvalidOperationException(T("Download ist unvollständig.", "Download is incomplete."));
                string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (release.Sha256 != null && hash != release.Sha256)
                    throw new InvalidOperationException(T("Prüfsumme stimmt nicht – Update abgebrochen.", "Checksum mismatch – update cancelled."));
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exePath, old);
            try
            {
                File.Move(download, exePath);
            }
            catch
            {
                // Put the old version back so the app still starts
                File.Move(old, exePath);
                throw;
            }
        }
        finally
        {
            try { if (File.Exists(download)) File.Delete(download); } catch { }
        }
    }

    static string OldPath(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, Path.GetFileNameWithoutExtension(exePath) + ".old.exe");

    /// <summary>Removes the previous version left behind by an update.</summary>
    public static void CleanupOld()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(OldPath(exe))) File.Delete(OldPath(exe));
        }
        catch
        {
            // Still in use by the old process for a moment; the next start cleans up
        }
    }

    /// <summary>Starts the freshly installed exe; the caller exits right after.</summary>
    public static void StartNewVersion(string exePath) =>
        Process.Start(new ProcessStartInfo(exePath, $"--tray --updated --wait-pid {Environment.ProcessId}") { UseShellExecute = false });

    public static void OpenReleasePage(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
