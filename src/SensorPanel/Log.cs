namespace SensorPanel;

/// <summary>
/// Where messages go now that there is usually no console: the console if one is attached,
/// and SensorPanel.log beside the exe. Warnings are also raised to the tray as balloons.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>Raised for every warning logged with notify set, on whichever thread logged it.</summary>
    public static event Action<string>? Warned;

    /// <summary>Starts a fresh log file. Without this call (the command-line modes, a --png run) only the console is written.</summary>
    public static void Start(string path)
    {
        try
        {
            _file = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warn($"Cannot write {path}: {e.Message}");
        }
    }

    public static void Info(string message) => Write(Console.Out, message);

    /// <param name="notify">False to log the warning without a tray balloon.</param>
    /// <param name="balloon">The balloon's text, when it should be shorter than the logged message.</param>
    public static void Warn(string message, bool notify = true, string? balloon = null)
    {
        Write(Console.Error, message);
        if (notify) Warned?.Invoke(balloon ?? message);
    }

    private static void Write(TextWriter console, string message)
    {
        lock (Gate)
        {
            console.WriteLine(message);
            _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");
        }
    }
}
