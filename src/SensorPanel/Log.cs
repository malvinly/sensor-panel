namespace SensorPanel;

/// <summary>
/// Where messages go now that there is usually no console: the console if one is attached,
/// and SensorPanel.log beside the exe. Warnings are also raised to the tray as balloons.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>Raised for every warning, on whichever thread logged it.</summary>
    public static event Action<string>? Warned;

    public static string? FilePath { get; private set; }

    /// <summary>Starts a fresh log file. Without this call (the command-line modes, a --png run) only the console is written.</summary>
    public static void Start(string path)
    {
        try
        {
            _file = new StreamWriter(path, append: false) { AutoFlush = true };
            FilePath = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warn($"Cannot write {path}: {e.Message}");
        }
    }

    public static void Info(string message) => Write(Console.Out, message);

    public static void Warn(string message)
    {
        Write(Console.Error, message);
        Warned?.Invoke(message);
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
