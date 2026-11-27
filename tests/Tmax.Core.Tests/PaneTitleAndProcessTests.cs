using System.Collections.Concurrent;
using System.Diagnostics;
using Tmax.Core.Platform;
using Tmax.Core.Pty;

namespace Tmax.Core.Tests;

public sealed class PaneTitleAndProcessTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _dir = Directory.CreateTempSubdirectory("tmax-titles-").FullName;
    private readonly ConcurrentQueue<(string Id, string Title)> _events = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PaneTitleFiles Create() => new(Path.Combine(_dir, "p"), (id, t) => _events.Enqueue((id, t)));

    private async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "condition not met in time");
    }

    [Theory]
    [InlineData("  Build läuft  \nsecond line", "Build läuft")]
    [InlineData("﻿a\u0007b​c", "abc")]
    [InlineData("", "")]
    public void Titles_are_sanitized(string raw, string expected) => Assert.Equal(expected, PaneTitleFiles.Sanitize(raw));

    [Fact]
    public void Titles_are_capped_without_splitting_surrogates()
    {
        var title = PaneTitleFiles.Sanitize(new string('x', 119) + "😀😀");
        Assert.True(title.Length <= PaneTitleFiles.MaxTitleLength);
        Assert.False(char.IsHighSurrogate(title[^1]));
    }

    [Fact]
    public async Task Writing_the_file_reports_the_title_and_release_clears_it()
    {
        using var titles = Create();
        var path = titles.Register("pane-1");
        Assert.NotNull(path);

        File.WriteAllText(path, "Deploying\n");
        await WaitFor(() => titles.GetTitle("pane-1") == "Deploying");
        Assert.Contains(("pane-1", "Deploying"), _events);

        titles.Release("pane-1");
        Assert.Contains(("pane-1", ""), _events);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Disabled_panes_ignore_further_writes()
    {
        using var titles = Create();
        var path = titles.Register("p2")!;
        titles.Disable("p2");
        File.WriteAllText(path, "ignored");
        await Task.Delay(300);
        titles.Refresh("p2");
        Assert.Equal("", titles.GetTitle("p2"));
    }

    [Fact]
    public void Unsafe_ids_get_no_file() => Assert.Null(Create().Register("../evil"));

    [Fact]
    public void Directories_of_dead_processes_are_removed()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(Path.Combine(root, "999999"));
        Directory.CreateDirectory(Path.Combine(root, "4242"));
        Directory.CreateDirectory(Path.Combine(root, "keep-me"));

        var mine = PaneTitleFiles.PrepareProcessDir(root, pid => pid == 4242);

        Assert.EndsWith(Environment.ProcessId.ToString(), mine);
        Assert.False(Directory.Exists(Path.Combine(root, "999999")));
        Assert.True(Directory.Exists(Path.Combine(root, "4242")));
        Assert.True(Directory.Exists(Path.Combine(root, "keep-me")));
    }

    [Fact]
    public async Task Process_tree_finds_child_processes()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1") { CreateNoWindow = true }
            : new ProcessStartInfo("sleep", "30");
        using var child = Process.Start(psi)!;
        try
        {
            var names = await ProcessTree.GetDescendantNamesAsync(Environment.ProcessId);
            Assert.Contains(OperatingSystem.IsWindows() ? "ping" : "sleep", names);
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public async Task Shell_gets_title_file_terminal_env_and_expanded_cwd()
    {
        if (OperatingSystem.IsWindows()) return; // covered by the POSIX shell syntax below
        var titles = new PaneTitleFiles(Path.Combine(_dir, "pty"), (id, t) => _events.Enqueue((id, t)));
        await using var manager = new PtyManager(() => [new ShellProfile("sh", "sh", "/bin/sh", [])], titles);
        var output = new ConcurrentQueue<string>();
        manager.DataReceived += (_, d) => output.Enqueue(d);

        var info = await manager.CreateAsync(new PtyCreateOptions { Id = "env", Cwd = "~" });
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), info.Cwd);

        await manager.WriteAsync("env", "echo \"[$TERM_PROGRAM]\" > /dev/null; printf 'Agent busy\\n' > \"$TMAX_TITLE_FILE\"; echo tp=$TERM_PROGRAM\r");
        await WaitFor(() => manager.GetExternalTitle("env") == "Agent busy");
        await WaitFor(() => string.Concat(output).Contains("tp=vscode"));
        Assert.True(manager.GetStats("env")!.WriteCount >= 1);
        Assert.True(manager.GetStats("env")!.DataBytes > 0);
    }
}
