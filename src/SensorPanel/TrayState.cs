using System.Text.Json;
using System.Text.Json.Serialization;

namespace SensorPanel;

/// <summary>
/// state.json beside the exe: what was picked in the tray menu, for the next start. The app
/// writes all of it on every pick, so unlike settings.json it is not meant for editing.
/// Used on the UI thread only, apart from being read once before the message loop runs.
/// </summary>
internal sealed class TrayState
{
    // Not Config.Options, which settings.json shares: this file is the app's own, so it allows no
    // comments, and a key that a later version drops or renames is skipped rather than costing the whole file.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private string? _savePath;     // null: never written (a --png run, or a file that could not be read)

    /// <summary>The last config picked that loaded, relative to the configs folder, like "default\default.json".</summary>
    public string? Config { get; set; }

    /// <summary>
    /// From the Brightness menu, over every other brightness; null for none. The menu offers 10-100
    /// (at 0 the panel looks switched off), but any 0-100 is taken, as settings.json's is.
    /// </summary>
    public int? Brightness { get; set; }

    /// <summary>
    /// The file's contents, or nothing remembered if there is no file or no <paramref name="path"/>.
    /// One that will not parse is reported, and replaced at the next pick. One that cannot be read
    /// (held by another program) is reported, and not written this run, so as not to lose what it holds.
    /// A brightness outside 0-100 is reported and ignored; the rest of the file still counts.
    /// </summary>
    /// <param name="writable">False to read only: a --png run beside the tray instance changes nothing.</param>
    public static TrayState Load(string? path, bool writable)
    {
        TrayState state = new();
        if (path is not null && File.Exists(path))
        {
            try { state = JsonSerializer.Deserialize<TrayState>(File.ReadAllText(path), Options) ?? new(); }
            catch (JsonException e)
            {
                Log.Warn($"Cannot read {path}, so it is ignored:\n  {e.Message}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Cannot read {path}, so what the tray menu picks is not remembered this run:\n  {e.Message}");
                writable = false;
            }
            if (state.Brightness is not (null or (>= 0 and <= 100)))
            {
                Log.Warn($"{path}: brightness {state.Brightness} is not 0-100, so it is ignored.");
                state.Brightness = null;
            }
        }
        state._savePath = writable ? path : null;
        return state;
    }

    /// <summary>Written under another name and renamed, so a write cut short never leaves a file that will not parse.</summary>
    public void Save()
    {
        if (_savePath is null) return;
        string staging = _savePath + ".new";
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(this, Options));
            File.Move(staging, _savePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot remember the tray's choices in {_savePath}: {e.Message}");
        }
    }
}
