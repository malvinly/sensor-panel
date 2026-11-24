using System.Xml.Linq;

namespace SensorPanel.Tests;

/// <summary>
/// <see cref="LogonTask"/>: the task the tray menu's "Start at logon" registers, and how it is
/// recognised again. Registering it needs administrator rights, so is not tried here.
/// </summary>
public class LogonTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\SensorPanel\SensorPanel.exe";
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static string Get(XDocument task, string name) => task.Descendants(Ns + name).Single().Value;

    [Fact]
    public void Starts_the_exe_elevated_on_the_users_desktop_10_s_after_their_logon_and_never_stops_it()
    {
        string xml = LogonTask.Xml(Exe, Sid);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);    // the file is written as UTF-16
        var task = XDocument.Parse(xml);

        Assert.Equal(Exe, Get(task, "Command"));
        Assert.Equal(@"C:\Program Files\SensorPanel", Get(task, "WorkingDirectory"));
        Assert.Equal(Sid, task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("PT10S", Get(task, "Delay"));
        var principal = task.Descendants(Ns + "Principal").Single();
        Assert.Equal(Sid, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", Get(task, "LogonType"));
        Assert.Equal("HighestAvailable", Get(task, "RunLevel"));
        // The action runs as that principal: Task Scheduler rejects a Context naming no principal.
        Assert.Equal(principal.Attribute("id")!.Value, task.Descendants(Ns + "Actions").Single().Attribute("Context")!.Value);
        Assert.Equal("PT0S", Get(task, "ExecutionTimeLimit"));
        Assert.Equal("5", Get(task, "Priority"));
        Assert.Equal("IgnoreNew", Get(task, "MultipleInstancesPolicy"));
        Assert.Equal("false", Get(task, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Get(task, "StopIfGoingOnBatteries"));
    }

    [Fact]
    public void A_path_with_xml_characters_survives_the_trip_both_ways()
    {
        const string exe = @"C:\Tools & <Things>\SensorPanel.exe";
        string xml = LogonTask.Xml(exe, Sid);
        Assert.Equal(exe, Get(XDocument.Parse(xml), "Command"));
        Assert.True(LogonTask.Runs(xml, exe));
    }

    [Fact]
    public void Its_own_task_is_recognised_ignoring_case()
    {
        Assert.True(LogonTask.Runs(LogonTask.Xml(Exe, Sid), Exe));
        Assert.True(LogonTask.Runs(LogonTask.Xml(Exe.ToUpperInvariant(), Sid), Exe));
    }

    [Fact]
    public void A_task_for_an_exe_elsewhere_is_not()
    {
        // The exe has been moved since the task was made.
        Assert.False(LogonTask.Runs(LogonTask.Xml(@"D:\Old\SensorPanel.exe", Sid), Exe));
    }

    [Fact]
    public void A_command_quoted_by_Task_Schedulers_editor_is_recognised()
    {
        string edited = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions Context="Author">
                <Exec>
                  <Command>"{Exe}"</Command>
                </Exec>
              </Actions>
            </Task>
            """;
        Assert.True(LogonTask.Runs(edited, Exe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERROR: The system cannot find the file specified.")]
    [InlineData("<Task><Actions><Exec><Command>C:\\Program Files\\SensorPanel\\SensorPanel.exe")]     // cut short
    public void Anything_else_is_not(string output) => Assert.False(LogonTask.Runs(output, Exe));
}
