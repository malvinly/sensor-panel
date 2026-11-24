using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using SensorPanel.Display;
using SensorPanel.Sensors;

namespace SensorPanel.Layout;

/// <summary>The JSON config file. Widgets are drawn in list order, later ones on top.</summary>
public sealed class Config
{
    /// <summary>Shown in the tray menu and tooltip instead of the file name. Optional.</summary>
    public string? Name { get; set; }
    public int RefreshMs { get; set; } = 1000;
    public Orientation Orientation { get; set; } = Orientation.Landscape;
    /// <summary>0-100, overriding settings.json's "brightness" for this config. Optional.</summary>
    public int? Brightness { get; set; }
    public SKColor Background { get; set; } = SKColors.Black;
    /// <summary>
    /// Slower poll intervals in ms for expensive hardware, by sensor key group (the part of a
    /// key before the first dot): { "gpu": 3000 }. Anything not listed is polled every <see cref="RefreshMs"/>.
    /// </summary>
    public Dictionary<string, int> SensorIntervals { get; set; } = [];
    /// <summary>Darken the panel and stop polling while Windows has the monitors turned off.</summary>
    public bool PauseWhenDisplayOff { get; set; } = true;
    public List<Widget> Widgets { get; set; } = [];

    internal static readonly JsonSerializerOptions Options = new()     // also settings.json's (Program.ReadSettings)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowOutOfOrderMetadataProperties = true,                           // "type" need not come first
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,       // typos are errors, not silence
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new ColorConverter() },
    };

    /// <summary>
    /// What the panel is set to for this config, given settings.json's "upsideDown" and
    /// "brightness": the orientation turned 180° if the panel is mounted upside down, and the
    /// config's own brightness if it has one, else <paramref name="fallbackBrightness"/>.
    /// </summary>
    public (Orientation Orientation, int Brightness) ForPanel(bool upsideDown, int fallbackBrightness) =>
        (upsideDown ? Protocol.Turned(Orientation) : Orientation, Brightness ?? fallbackBrightness);

    /// <summary>Reads and checks the config. Every problem that can be seen without hardware is a JsonException here.</summary>
    public static Config Load(string path)
    {
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Options)
                     ?? throw new JsonException("Config file is empty.");

        if (config.RefreshMs < 100) throw new JsonException("refreshMs must be at least 100.");
        // The enum converter also takes plain numbers, which would go to the panel as they are.
        if (!Enum.IsDefined(config.Orientation))
            throw new JsonException("orientation must be one of: landscape, portrait, reverseLandscape, reversePortrait.");

        foreach (var (group, ms) in config.SensorIntervals)
        {
            if (!SensorHub.Groups.Contains(group))
                throw new JsonException(
                    $"sensorIntervals: \"{group}\" is not a hardware sensor group. Use one of: {string.Join(", ", SensorHub.Groups)}. " +
                    "Computed keys cannot be listed.");
            if (ms is <= 0 or > 3_600_000)
                throw new JsonException($"sensorIntervals: \"{group}\" must be between 1 and 3600000 milliseconds (one hour).");
        }

        foreach (var widget in config.Widgets) widget.Validate();
        return config;
    }

    /// <summary>
    /// The config's "name", or its file name without the extension when it has none. Only that
    /// property is read, so a config with a mistake elsewhere is still listed by its name; one
    /// that is not readable JSON at all is listed by its file name.
    /// </summary>
    public static string DisplayName(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()))
                return name.GetString()!.Trim();
        }
        catch (Exception) { }   // it is only a label: whatever goes wrong, the file name will do
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// The configs in a configs folder: every .json directly inside one of its subfolders. Each
    /// config keeps the pictures it draws in its own folder, so copying the folder copies all of it.
    /// </summary>
    public static string[] Files(string configs) =>
        Directory.GetDirectories(configs).SelectMany(dir => Directory.GetFiles(dir, "*.json")).ToArray();

    /// <summary>Configs in tray menu order: by display name, then by path when two names are the same.</summary>
    public static List<(string Path, string Name)> ByName(IEnumerable<string> paths) =>
        paths.Select(p => (Path: p, Name: DisplayName(p)))
             .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
             .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>"#RRGGBB" or "#AARRGGBB".</summary>
    private sealed class ColorConverter : JsonConverter<SKColor>
    {
        public override SKColor Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            SKColor.TryParse(reader.GetString(), out var color)
                ? color
                : throw new JsonException($"'{reader.GetString()}' is not a colour; use \"#RRGGBB\" or \"#AARRGGBB\".");

        public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
