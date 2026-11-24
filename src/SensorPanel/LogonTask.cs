using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace SensorPanel;

/// <summary>
/// The tray menu's "Start at logon": a Task Scheduler task that starts the exe at logon. Not the
/// Run registry key or the Startup folder: Windows does not elevate what those start, and the exe
/// requires administrator rights (see app.manifest). Made with schtasks.exe rather than Task
/// Scheduler's COM API, and the task's XML is written and read as plain text rather than with
/// System.Xml, so nothing new stays loaded in this process. A query takes ~25 ms.
/// Nothing here throws: a failure to change the task is a warning, one to query it reads as off.
/// </summary>
internal static class LogonTask
{
    private const string Name = "SensorPanel";
    private static readonly string Schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>
    /// Whether the task exists and starts this exe. Any failure to ask, not only a missing task,
    /// reads as no. A task that has been disabled in Task Scheduler still counts.
    /// </summary>
    public static bool IsOn(string exe)
    {
        var result = Run("/Query", "/TN", Name, "/XML");
        return result.Code == 0 && Runs(result.Output, exe);
    }

    /// <summary>Creates the task for this exe, replacing one of the same name.</summary>
    public static void TurnOn(string exe)
    {
        string xml;
        using (var me = WindowsIdentity.GetCurrent()) xml = Xml(exe, me.User!.Value);
        // schtasks reads the file's encoding from its declaration, which Xml writes as UTF-16.
        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        // %TEMP% is writable by every unelevated program of this user, and whatever schtasks reads
        // runs elevated at each logon. So the file gets a name nothing can guess, and is then held
        // open while schtasks runs, letting others read it but not change, replace or delete it,
        // and checked first for anything changed in between.
        string file = Path.Combine(Path.GetTempPath(), $"SensorPanel-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllBytes(file, bytes);
            using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var written = new byte[bytes.Length + 1];
            if (hold.ReadAtLeast(written, written.Length, throwOnEndOfStream: false) != bytes.Length
                || !written.AsSpan(0, bytes.Length).SequenceEqual(bytes))
            {
                Log.Warn($"Start at logon: cannot create the {Name} task: {file} was changed by another program.");
                return;
            }
            Report("create", "created", Run("/Create", "/TN", Name, "/XML", file, "/F"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Start at logon: cannot create the {Name} task: {e.Message}");
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }     // a leftover temp file harms nothing
        }
    }

    public static void TurnOff() => Report("delete", "deleted", Run("/Delete", "/TN", Name, "/F"));

    /// <summary>
    /// The task: at this user's logon, 10 s later (so as not to add to the logon rush; a panel or
    /// network that is not ready yet is retried anyway), run the exe with highest privileges, so
    /// with no UAC prompt, on this user's desktop ("InteractiveToken": the icon needs it). Settings
    /// that differ from Task Scheduler's defaults: the battery conditions (both default to true),
    /// no time limit (the default stops it after 3 days), and priority 5, normal (the default, 7,
    /// is below normal with low I/O priority). The exe's own folder is the working folder, as when
    /// it is started from Explorer, though the app finds its files from its own path anyway.
    /// </summary>
    /// <param name="user">The user's SID.</param>
    internal static string Xml(string exe, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts SensorPanel at logon. Made by its tray menu's "Start at logon".</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <UserId>{Escape(user)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exe)}</Command>
              <WorkingDirectory>{Escape(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>
    /// Whether a task's XML, as <c>schtasks /Query /XML</c> prints it, starts <paramref name="exe"/>.
    /// The Command is unquoted as <see cref="Xml"/> writes it, but quoted once the program is picked
    /// in Task Scheduler's own editor, which adds quotes to a path with spaces.
    /// </summary>
    internal static bool Runs(string taskXml, string exe)
    {
        const string Open = "<Command>", Close = "</Command>";
        int start = taskXml.IndexOf(Open, StringComparison.Ordinal);
        int end = start < 0 ? -1 : taskXml.IndexOf(Close, start, StringComparison.Ordinal);
        if (end < 0) return false;
        string command = Unescape(taskXml[(start + Open.Length)..end]).Trim().Trim('"');
        return command.Equals(exe, StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    private static void Report(string verb, string done, (int Code, string Output, string Error) result)
    {
        if (result.Code == 0) Log.Info($"Start at logon: {done} the {Name} task.");
        else Log.Warn($"Start at logon: cannot {verb} the {Name} task: {(result.Error + result.Output).Trim()}");
    }

    /// <summary>Runs schtasks.exe, on the UI thread, so for 10 s at most. Code is -1 when it could not run.</summary>
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var start = new ProcessStartInfo(Schtasks)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();     // both at once, so neither pipe fills and blocks
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return (-1, "", "schtasks.exe did not finish within 10 s.");
            }
            return (process.ExitCode, output.Result, error.Result);
        }
        catch (Win32Exception e) { return (-1, "", e.Message); }     // could not be started (missing, or blocked by policy)
    }
}
