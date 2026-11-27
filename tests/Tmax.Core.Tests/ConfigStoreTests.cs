using System.Text.Json.Nodes;
using Tmax.Core.Config;
using Tmax.Core.Pty;

namespace Tmax.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tmax-config-test-").FullName;
    private string ConfigPath => Path.Combine(_dir, "tmax-config.json");

    private static JsonObject Defaults() => DefaultConfig.Create([new ShellProfile("sh", "sh", "/bin/sh", [])]);

    public void Dispose() => TestDirs.Delete(_dir);

    [Fact]
    public void First_start_writes_defaults_in_the_original_shape()
    {
        var store = new ConfigStore(ConfigPath, Defaults());

        Assert.True(File.Exists(ConfigPath));
        var all = store.GetAll();
        Assert.Equal("sh", all["defaultShellId"]!.GetValue<string>());
        Assert.Equal("/bin/sh", all["shells"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(50000, all["terminal"]!["scrollback"]!.GetValue<int>());
        Assert.Contains(all["keybindings"]!.AsArray(), b => b!["key"]!.GetValue<string>() == "Ctrl+T");
    }

    [Fact]
    public void Values_survive_a_restart_and_missing_keys_get_defaults()
    {
        File.WriteAllText(ConfigPath, """{ "tabMode": "workspaces" }""");

        var store = new ConfigStore(ConfigPath, Defaults());
        store.Set("backgroundOpacity", JsonValue.Create(0.5));

        var reloaded = new ConfigStore(ConfigPath, Defaults()).GetAll();
        Assert.Equal("workspaces", reloaded["tabMode"]!.GetValue<string>());
        Assert.Equal(0.5, reloaded["backgroundOpacity"]!.GetValue<double>());
        Assert.NotNull(reloaded["theme"]);
    }

    [Fact]
    public void Shells_from_config_are_the_spawn_allow_list()
    {
        var store = new ConfigStore(ConfigPath, Defaults());
        store.Set("shells", JsonNode.Parse("""[{ "id": "b", "name": "bash", "path": "/bin/bash", "args": ["-l"] }, { "id": "new", "name": "", "path": "", "args": [] }]"""));

        var shell = Assert.Single(store.Shells); // placeholder without a path is not spawnable
        Assert.Equal("/bin/bash", shell.Path);
        Assert.Equal(["-l"], shell.Args);
    }

    [Theory]
    [InlineData("""[{ "path": 42 }]""")]
    [InlineData("""{ "path": "/bin/sh" }""")]
    public void Malformed_shells_are_rejected(string json)
    {
        var store = new ConfigStore(ConfigPath, Defaults());
        Assert.Throws<ArgumentException>(() => store.Set("shells", JsonNode.Parse(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("__proto__")]
    [InlineData("a.b")]
    public void Invalid_keys_are_rejected(string key)
    {
        var store = new ConfigStore(ConfigPath, Defaults());
        Assert.Throws<ArgumentException>(() => store.Set(key, JsonValue.Create(1)));
    }

    [Fact]
    public void Session_round_trips_and_is_read_fresh_from_disk()
    {
        var path = Path.Combine(_dir, "tmax-session.json");
        var a = new SessionStore(path);
        var b = new SessionStore(path);
        Assert.Null(a.Load());

        a.Save(JsonNode.Parse("""{ "tabs": [1, 2] }"""));

        Assert.Equal(2, b.Load()!["tabs"]!.AsArray().Count);
        Assert.Contains("\"session\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task Session_writes_are_reported()
    {
        using var store = new SessionStore(Path.Combine(_dir, "tmax-session.json"));
        var changed = new TaskCompletionSource();
        store.Watch(() => changed.TrySetResult());
        await Task.Delay(100);
        store.Save(JsonNode.Parse("""{ "a": 1 }"""));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
