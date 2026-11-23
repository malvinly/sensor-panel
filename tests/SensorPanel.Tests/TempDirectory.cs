namespace SensorPanel.Tests;

/// <summary>A fresh folder under %TEMP%, deleted with everything in it on Dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SensorPanel.Tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Writes a file (creating its folders) and returns its full path.</summary>
    public string Write(string name, string contents)
    {
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, name));     // "a/b" with Windows separators
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }     // a file still open (virus scanner); %TEMP% is cleaned up eventually
    }
}
