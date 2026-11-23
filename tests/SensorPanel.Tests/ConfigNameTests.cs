using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary><see cref="Config.DisplayName"/> and <see cref="Config.ByName"/>: the tray menu's labels and order.</summary>
public sealed class ConfigNameTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("""{ "name": "Gaming" }""", "Gaming")]
    [InlineData("""{ "name": "  Gaming \t" }""", "Gaming")]
    [InlineData("""{ /* first */ "name": "Gaming", }""", "Gaming")]
    [InlineData("""{ "name": "Gaming", "refreshMS": 1 }""", "Gaming")]     // a mistake elsewhere does not lose the name
    [InlineData("""{ "refreshMs": 1000 }""", "my-layout")]
    [InlineData("""{ "name": "   " }""", "my-layout")]
    [InlineData("""{ "name": "" }""", "my-layout")]
    [InlineData("""{ "name": 3 }""", "my-layout")]
    [InlineData("""{ "name": null }""", "my-layout")]
    [InlineData("""[ { "name": "Gaming" } ]""", "my-layout")]
    [InlineData("""{ "name": "Gaming" """, "my-layout")]             // not readable JSON
    [InlineData("", "my-layout")]
    public void Uses_the_name_or_else_the_file_name(string json, string expected) =>
        Assert.Equal(expected, Config.DisplayName(_dir.Write("my-layout.json", json)));

    [Fact]
    public void A_missing_file_is_listed_by_its_file_name() =>
        Assert.Equal("gone", Config.DisplayName(Path.Combine(_dir.Path, "gone.json")));

    [Fact]
    public void Sorted_by_name_ignoring_case_then_by_path()
    {
        string b = _dir.Write("b.json", """{ "name": "beta" }""");
        string a = _dir.Write("a.json", """{ "name": "Alpha" }""");
        string z = _dir.Write("z.json", """{ "name": "alpha" }""");     // the same name as a.json but for case
        string c = _dir.Write("c.json", "{}");                           // listed as "c"

        var sorted = Config.ByName([z, b, c, a]);

        Assert.Equal([a, z, b, c], sorted.Select(s => s.Path));
        Assert.Equal(["Alpha", "alpha", "beta", "c"], sorted.Select(s => s.Name));
    }
}
