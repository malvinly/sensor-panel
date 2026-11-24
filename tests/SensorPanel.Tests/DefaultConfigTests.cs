using SensorPanel.Layout;

namespace SensorPanel.Tests;

/// <summary>
/// The configs folder beside the exe: written out on first run, and the config to start with
/// (or fall back on) when none was picked. Each test works in its own temp folder.
/// </summary>
public sealed class DefaultConfigTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private string Configs => Path.Combine(_dir.Path, "configs");
    private string Default => Path.Combine(Configs, "default", "default.json");
    private string Background => Path.Combine(Configs, "default", "background.png");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void First_run_writes_the_default_layout_its_background_and_the_gaming_layout()
    {
        Program.WriteDefaultConfigs(Configs);

        Assert.True(File.Exists(Default));
        Assert.True(File.Exists(Background));
        Assert.True(File.Exists(Path.Combine(Configs, "gaming", "gaming.json")));
        Assert.False(Directory.Exists(Configs + ".new"), "the staging folder is renamed, not left behind");
    }

    [Fact]
    public void An_existing_configs_folder_is_left_alone()
    {
        string mine = _dir.Write("configs/mine/mine.json", "{}");
        Program.WriteDefaultConfigs(Configs);

        Assert.Equal([Path.GetDirectoryName(mine)!], Directory.GetFileSystemEntries(Configs));
        Assert.False(Directory.Exists(Configs + ".new"), "nothing is even staged");
    }

    [Fact]
    public void A_staging_folder_left_by_a_failed_run_is_replaced()
    {
        _dir.Write("configs.new/half/half-written.json", "{");
        Program.WriteDefaultConfigs(Configs);

        Assert.True(File.Exists(Default));
        Assert.False(Directory.Exists(Path.Combine(Configs, "half")));
    }

    [Fact]
    public void Starts_with_default_json_when_it_is_there()
    {
        _dir.Write("configs/aaa/aaa.json", """{ "name": "AAA" }""");
        _dir.Write("configs/default/default.json", "{}");
        Assert.Equal(Default, Program.DefaultConfig(Configs));
    }

    [Fact]
    public void Without_default_json_starts_with_the_first_config_in_the_menu()
    {
        _dir.Write("configs/a/a.json", """{ "name": "Zed" }""");
        string alpha = _dir.Write("configs/z/z.json", """{ "name": "Alpha" }""");     // first by name, not by file name
        Assert.Equal(alpha, Program.DefaultConfig(Configs));
        Assert.False(File.Exists(Default), "default/default.json is not written back while other configs remain");
    }

    [Fact]
    public void An_empty_configs_folder_gets_default_json_back()
    {
        Directory.CreateDirectory(Configs);
        Assert.Equal(Default, Program.DefaultConfig(Configs));
        Assert.True(File.Exists(Default));
        Assert.True(File.Exists(Background));
    }

    [Fact]
    public void Configs_are_the_json_files_directly_inside_each_subfolder()
    {
        string a = _dir.Write("configs/a/a.json", "{}");
        string b = _dir.Write("configs/b/variant.json", "{}");      // the file need not be named after its folder
        _dir.Write("configs/loose.json", "{}");                      // not in a folder of its own
        _dir.Write("configs/b/slides/data.json", "{}");              // one of b's own files
        _dir.Write("configs/b/background.png", "");
        Assert.Equal([a, b], Config.Files(Configs).Order());
    }

    [Fact]
    public void A_loose_json_file_does_not_count_as_a_config()
    {
        _dir.Write("configs/old.json", """{ "name": "AAA" }""");
        Assert.Equal(Default, Program.DefaultConfig(Configs));
        Assert.True(File.Exists(Default), "the folder had no configs, so default/default.json is written back");
    }

    [Fact]
    public void Restoring_default_json_keeps_an_edited_background()
    {
        string background = _dir.Write("configs/default/background.png", "my own picture");
        Program.DefaultConfig(Configs);
        Assert.True(File.Exists(Default));
        Assert.Equal("my own picture", File.ReadAllText(background));
    }

    [Fact]
    public void A_missing_configs_folder_is_not_created_by_the_fallback()
    {
        // The first-run write failed; making the folder here would stop it from ever being tried again.
        Assert.Equal(Default, Program.DefaultConfig(Configs));
        Assert.False(Directory.Exists(Configs));
    }
}
