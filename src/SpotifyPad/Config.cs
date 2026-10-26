using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyPad;

public enum PadAction { PlayPause, Next, Previous, VolumeUp, VolumeDown, Mute }

/// <summary>Spotify = slider inside the Spotify app, Mixer = Windows volume mixer entry, System = master volume.</summary>
public enum VolumeTarget { Spotify, System, Mixer }

public static class PadActions
{
    public static readonly PadAction[] All = Enum.GetValues<PadAction>();

    public static string Label(PadAction a) => a switch
    {
        PadAction.PlayPause => "Play / Pause",
        PadAction.Next => T("Nächster Song (Skip)", "Next song (skip)"),
        PadAction.Previous => T("Vorheriger Song", "Previous song"),
        PadAction.VolumeUp => T("Lauter", "Volume up"),
        PadAction.VolumeDown => T("Leiser", "Volume down"),
        PadAction.Mute => T("Stumm an/aus", "Mute on/off"),
        _ => a.ToString(),
    };
}

public sealed class PadBinding
{
    public PadAction Action { get; set; }
    public List<PadButton> Buttons { get; set; } = [];

    [JsonIgnore]
    public uint Mask => Buttons.Aggregate(0u, (m, b) => m | (uint)b);

    public static List<PadButton> FromMask(uint mask) =>
        PadButtons.All.Where(b => (mask & (uint)b.Button) != 0).Select(b => b.Button).ToList();
}

/// <summary>A named set of bindings, one list per controller type.</summary>
public sealed class Preset
{
    public string Name { get; set; } = "Standard";
    public List<PadBinding> Xbox { get; set; } = AppConfig.DefaultBindings();
    public List<PadBinding> PlayStation { get; set; } = AppConfig.DefaultBindings();

    public List<PadBinding> For(PadType type) => type == PadType.Xbox ? Xbox : PlayStation;

    public void Set(PadType type, List<PadBinding> list)
    {
        if (type == PadType.Xbox) Xbox = list;
        else PlayStation = list;
    }
}

public sealed class AppConfig
{
    public bool Enabled { get; set; } = true;
    public AppLanguage Language { get; set; } = AppLanguage.Auto;
    public bool SpotifyDirect { get; set; } = true;
    public VolumeTarget VolumeTarget { get; set; } = VolumeTarget.Spotify;
    public int VolumeStep { get; set; } = 5;
    public bool Rumble { get; set; } = true;
    /// <summary>Keep Spotify's autostart and shortcuts launching with the DevTools flag.</summary>
    public bool PatchSpotifyLaunch { get; set; }
    /// <summary>Restart Spotify with the DevTools flag when it gets opened without it.</summary>
    public bool AutoRestartSpotify { get; set; } = true;
    /// <summary>Start SpotifyPad with Windows; null in older configs (then taken from the registry).</summary>
    public bool? StartWithWindows { get; set; }
    public int TriggerThreshold { get; set; } = 128;
    public List<Preset> Presets { get; set; } = [];
    public string ActivePreset { get; set; } = "Standard";

    // Pre-preset config files stored the bindings here; only read for migration
    public List<PadBinding>? Bindings { get; set; }
    public List<PadBinding>? PlayStationBindings { get; set; }

    [JsonIgnore]
    public Preset Active => Presets.FirstOrDefault(p => p.Name == ActivePreset) ?? Presets[0];

    public List<PadBinding> BindingsFor(PadType type) => Active.For(type);
    public void SetBindings(PadType type, List<PadBinding> list) => Active.Set(type, list);

    /// <summary>Returns <paramref name="name"/>, or "name (2)", "name (3)"… if a preset already uses it.</summary>
    public string UniqueName(string name)
    {
        string candidate = name;
        for (int i = 2; Presets.Any(p => p.Name == candidate); i++) candidate = $"{name} ({i})";
        return candidate;
    }

    void Normalize()
    {
        if (Presets.Count == 0)
            Presets.Add(new Preset { Name = "Standard", Xbox = Bindings ?? DefaultBindings(), PlayStation = PlayStationBindings ?? DefaultBindings() });
        Bindings = null;
        PlayStationBindings = null;
        if (!Presets.Any(p => p.Name == ActivePreset)) ActivePreset = Presets[0].Name;
    }

    static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyPad");
    static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Options) is { } cfg)
            {
                cfg.Normalize();
                return cfg;
            }
        }
        catch
        {
            // Broken config: fall back to defaults
        }
        return Default();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
    }

    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Options), Options)!;

    public static List<PadBinding> DefaultBindings() =>
    [
        new() { Action = PadAction.PlayPause, Buttons = [PadButton.LB, PadButton.RB, PadButton.A] },
        new() { Action = PadAction.Next, Buttons = [PadButton.LB, PadButton.RB, PadButton.DPadRight] },
        new() { Action = PadAction.Previous, Buttons = [PadButton.LB, PadButton.RB, PadButton.DPadLeft] },
        new() { Action = PadAction.VolumeUp, Buttons = [PadButton.LB, PadButton.RB, PadButton.DPadUp] },
        new() { Action = PadAction.VolumeDown, Buttons = [PadButton.LB, PadButton.RB, PadButton.DPadDown] },
        new() { Action = PadAction.Mute, Buttons = [PadButton.LB, PadButton.RB, PadButton.Y] },
    ];

    public static AppConfig Default()
    {
        var cfg = new AppConfig();
        cfg.Normalize();
        return cfg;
    }
}
