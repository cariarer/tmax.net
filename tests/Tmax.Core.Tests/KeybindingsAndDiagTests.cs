using System.Text.Json.Nodes;
using Tmax.Core.Config;

namespace Tmax.Core.Tests;

public sealed class KeybindingsAndDiagTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tmax-kb-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Comments_trailing_commas_and_bad_entries_are_tolerated()
    {
        var warnings = new List<string>();
        var bindings = KeybindingsFile.Parse("""
            // header
            [
              { "key": "Ctrl+T", "action": "createTerminal" }, // inline "quoted // comment"
              { "key": "", "action": "x" },
              42,
              { "key": "Ctrl+//", "action": "commandPalette" },
            ]
            """, warnings.Add);

        Assert.Equal([new("Ctrl+T", "createTerminal"), new("Ctrl+//", "commandPalette")], bindings);
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Init_writes_a_documented_file_that_round_trips()
    {
        var file = new KeybindingsFile(_dir);
        var read = file.Init([new("Ctrl+T", "createTerminal")]);

        Assert.Equal([new Keybinding("Ctrl+T", "createTerminal")], read);
        var text = File.ReadAllText(file.Path);
        Assert.StartsWith("// tmax keybindings", text);
        Assert.Contains("//   - goToWorkspace9", text);

        File.WriteAllText(file.Path, "[]");
        Assert.Empty(file.Init([new("Ctrl+X", "y")])); // existing file wins
    }

    [Fact]
    public async Task Edits_on_disk_are_reported()
    {
        var file = new KeybindingsFile(_dir);
        file.Init([]);
        var changed = new TaskCompletionSource<List<Keybinding>>();
        file.Changed += b => changed.TrySetResult(b);
        file.StartWatching();

        File.WriteAllText(file.Path, """[{ "key": "Ctrl+K", "action": "commandPalette" }]""");

        var bindings = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Ctrl+K", Assert.Single(bindings).Key);
        file.Dispose();
    }

    [Fact]
    public void Diag_log_redacts_home_and_user_and_reads_tail()
    {
        using var log = new DiagLog(Path.Combine(_dir, "diag.log"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        log.Log("pty:created", new JsonObject
        {
            ["cwd"] = Path.Combine(home, "repos"),
            ["other"] = "/home/alice/x",
            ["n"] = 3,
        });

        var tail = log.ReadTail();
        Assert.Contains("pty:created", tail);
        Assert.DoesNotContain(home, tail);
        Assert.Contains("/home/<user>/x", tail);
        Assert.Contains("\"n\":3", tail);
    }
}
