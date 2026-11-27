using System.Collections.Concurrent;
using System.Text;
using Tmax.Core.Pty;

namespace Tmax.Core.Tests;

public class PtyManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static ShellProfile TestShell() => OperatingSystem.IsWindows()
        ? new("cmd", "cmd", Path.Combine(Environment.SystemDirectory, "cmd.exe"), [])
        : new("sh", "sh", "/bin/sh", []);

    [Fact]
    public async Task Spawned_shell_echoes_output_and_exposes_it_in_the_buffer()
    {
        await using var manager = new PtyManager([TestShell()]);
        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        manager.DataReceived += (id, data) =>
        {
            lock (output)
            {
                output.Append(data);
                if (output.ToString().Contains("tmax-ok-42")) seen.TrySetResult();
            }
        };

        var info = await manager.CreateAsync(new PtyCreateOptions { Id = "t1", Cols = 100, Rows = 30 });
        Assert.True(info.Pid > 0);

        // Split the marker so the echoed command line itself does not satisfy the check.
        await manager.WriteAsync("t1", "echo tmax-ok-$((40+2))\r");
        if (OperatingSystem.IsWindows()) await manager.WriteAsync("t1", "echo tmax-ok-42\r");

        await seen.Task.WaitAsync(Timeout);
        Assert.Contains("tmax-ok-42", manager.Get("t1")!.Output.Snapshot());
    }

    [Fact]
    public async Task Exit_is_reported_once_and_session_is_removed()
    {
        await using var manager = new PtyManager([TestShell()]);
        var exits = new ConcurrentBag<string>();
        var exited = new TaskCompletionSource();
        manager.Exited += (id, _) => { exits.Add(id); exited.TrySetResult(); };

        await manager.CreateAsync(new PtyCreateOptions { Id = "t2" });
        await manager.WriteAsync("t2", "exit\r");

        await exited.Task.WaitAsync(Timeout);
        await Task.Delay(300); // give a potential duplicate exit event a chance to show up
        Assert.Single(exits);
        Assert.Null(manager.Get("t2"));
    }

    [Fact]
    public async Task Writes_racing_with_shell_exit_do_not_throw()
    {
        await using var manager = new PtyManager([TestShell()]);
        await manager.CreateAsync(new PtyCreateOptions { Id = "t5" });
        var session = manager.Get("t5")!;

        await session.WriteAsync("exit\r");
        var deadline = DateTime.UtcNow + Timeout;
        while (!session.HasExited && DateTime.UtcNow < deadline)
            await session.WriteAsync("x"); // keeps writing straight through the exit

        Assert.True(session.HasExited);
    }

    [Fact]
    public async Task Shell_outside_profiles_is_rejected()
    {
        await using var manager = new PtyManager([TestShell()]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            manager.CreateAsync(new PtyCreateOptions { Id = "t3", ShellPath = "/usr/bin/env" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil")]
    [InlineData("a b")]
    public async Task Invalid_pane_ids_are_rejected(string id)
    {
        await using var manager = new PtyManager([TestShell()]);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateAsync(new PtyCreateOptions { Id = id }));
    }

    [Fact]
    public async Task Pane_id_is_exported_to_the_shell()
    {
        await using var manager = new PtyManager([TestShell()]);
        var output = new StringBuilder();
        var seen = new TaskCompletionSource();
        manager.DataReceived += (_, data) =>
        {
            lock (output)
            {
                output.Append(data);
                if (output.ToString().Contains("pane=t4!")) seen.TrySetResult();
            }
        };

        await manager.CreateAsync(new PtyCreateOptions { Id = "t4" });
        await manager.WriteAsync("t4", OperatingSystem.IsWindows() ? "echo pane=%TMAX_PANE_ID%!\r" : "echo pane=$TMAX_PANE_ID!\r");

        await seen.Task.WaitAsync(Timeout);
    }

    [Fact]
    public void Output_buffer_keeps_only_the_tail()
    {
        var buffer = new OutputBuffer(capacityChars: 10);
        buffer.Append("0123456789");
        buffer.Append("abc");
        Assert.Equal("3456789abc", buffer.Snapshot());
        Assert.Equal(13, buffer.TotalWritten);
    }
}
