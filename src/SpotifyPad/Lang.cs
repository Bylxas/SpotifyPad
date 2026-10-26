global using static SpotifyPad.Lang;
using System.Runtime.InteropServices;

namespace SpotifyPad;

public enum AppLanguage { Auto, German, English }

/// <summary>
/// Minimal UI localization: every text is written as a German/English pair at its use site, e.g. T("Speichern", "Save").
/// </summary>
public static class Lang
{
    public static bool English { get; private set; }

    public static string T(string german, string english) => English ? english : german;

    /// <summary>Auto follows the Windows display language (German → German, everything else → English).</summary>
    public static void Apply(AppLanguage language)
    {
        English = language switch
        {
            AppLanguage.German => false,
            AppLanguage.English => true,
            // The app uses invariant globalization, so ask Windows directly
            _ => (GetUserDefaultUILanguage() & 0x3FF) != LANG_GERMAN,
        };
    }

    const int LANG_GERMAN = 0x07;

    [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();
}
