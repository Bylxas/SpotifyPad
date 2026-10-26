using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyPad;

/// <summary>Turns a preset into a short text code (JSON → Deflate → Base64url) and back.</summary>
static class ShareCode
{
    public const string Prefix = "SPAD1-";
    const int MaxBytes = 64 * 1024;
    const int MaxBindings = 64;
    const int MaxNameLength = 40;

    // Compact wire format: n = name, x / p = Xbox / PlayStation bindings as [action, buttonMask]
    // or [action, buttonMask, volume%] for volume presets,
    // s = controller type the sharer uses (missing in older codes)
    sealed record Dto(string n, uint[][] x, uint[][] p, int? s = null);

    static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The result of <see cref="Import"/>: the preset and which controller type it was made on, if known.</summary>
    public sealed record Imported(Preset Preset, PadType? Source);

    /// <param name="source">The controller type the sharer actually uses; lets the importer convert to their controller.</param>
    public static string Export(Preset preset, PadType? source = null)
    {
        var dto = new Dto(preset.Name, Pack(preset.Xbox), Pack(preset.PlayStation), (int?)source);
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, Options);

        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.SmallestSize))
            deflate.Write(json);
        return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <exception cref="FormatException">The code is not a valid SpotifyPad code.</exception>
    public static Imported Import(string code)
    {
        code = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException(T("Der Code muss mit „" + Prefix + "“ beginnen.", "The code must start with \"" + Prefix + "\"."));

        try
        {
            string b64 = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');

            using var input = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            // Bounded copy so a malicious code can't inflate to gigabytes
            var buffer = new byte[4096];
            int n;
            while ((n = input.Read(buffer)) > 0)
            {
                if (output.Length + n > MaxBytes) throw new FormatException(T("Code ist zu groß.", "Code is too large."));
                output.Write(buffer, 0, n);
            }

            var dto = JsonSerializer.Deserialize<Dto>(output.ToArray(), Options) ?? throw new FormatException(T("Leerer Code.", "Empty code."));
            string name = (dto.n ?? "").Trim();
            if (name.Length == 0) name = T("Importiert", "Imported");
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];
            var preset = new Preset { Name = name, Xbox = Unpack(dto.x), PlayStation = Unpack(dto.p) };
            PadType? source = dto.s is int s and (0 or 1) ? (PadType)s : GuessSource(preset);
            return new Imported(preset, source);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new FormatException(T("Der Code ist ungültig oder unvollständig.", "The code is invalid or incomplete."));
        }
    }

    /// <summary>Older codes: if only one list differs from the defaults, that is the one the sharer edited.</summary>
    static PadType? GuessSource(Preset preset)
    {
        bool xboxCustom = !SameAsDefault(preset.Xbox), psCustom = !SameAsDefault(preset.PlayStation);
        return (xboxCustom, psCustom) switch
        {
            (true, false) => PadType.Xbox,
            (false, true) => PadType.PlayStation,
            _ => null,
        };
    }

    static bool SameAsDefault(List<PadBinding> bindings) =>
        Pack(bindings).Select(b => (b[0], b[1])).SequenceEqual(Pack(AppConfig.DefaultBindings()).Select(b => (b[0], b[1])));

    /// <summary>
    /// Copies the sharer's bindings onto the importer's controller type. Xbox and PlayStation buttons share
    /// the same positions (LB = L1, A = ✕ …), so the masks carry over unchanged.
    /// </summary>
    /// <returns>true if bindings were converted.</returns>
    public static bool ConvertTo(Imported imported, PadType target)
    {
        if (imported.Source is not { } source || source == target) return false;
        var list = imported.Preset.For(source)
            .Select(b => new PadBinding { Action = b.Action, Buttons = [.. b.Buttons], Volume = b.Volume })
            .ToList();
        imported.Preset.Set(target, list);
        return true;
    }

    static uint[][] Pack(List<PadBinding> bindings) =>
        bindings.Select(b => b.Volume is int v ? new[] { (uint)b.Action, b.Mask, (uint)v } : new[] { (uint)b.Action, b.Mask }).ToArray();

    static List<PadBinding> Unpack(uint[][]? items)
    {
        if (items == null) return [];
        if (items.Length > MaxBindings) throw new FormatException(T("Zu viele Belegungen im Code.", "Too many bindings in the code."));

        uint known = PadButtons.All.Aggregate(0u, (m, b) => m | (uint)b.Button);
        var list = new List<PadBinding>();
        foreach (var item in items)
        {
            var invalid = new FormatException(T("Ungültige Belegung im Code.", "Invalid binding in the code."));
            if (item is not ([_, _] or [_, _, _])) throw invalid;
            uint action = item[0], mask = item[1];
            int? volume = item.Length == 3 ? (int)item[2] : null;
            if (!Enum.IsDefined((PadAction)action) || mask == 0 || (mask & ~known) != 0 || volume > 100) throw invalid;
            if ((PadAction)action == PadAction.SetVolume) volume ??= 30;
            else volume = null;
            list.Add(new PadBinding { Action = (PadAction)action, Buttons = PadBinding.FromMask(mask), Volume = volume });
        }
        return list;
    }
}
