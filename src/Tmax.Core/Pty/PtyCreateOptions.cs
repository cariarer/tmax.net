namespace Tmax.Core.Pty;

/// <summary>
/// Options for spawning a terminal. Mirrors the renderer's <c>createPty</c> payload
/// from the original Electron app so the frontend can stay unchanged.
/// </summary>
public sealed record PtyCreateOptions
{
    /// <summary>Caller-chosen pane id (the renderer generates it).</summary>
    public required string Id { get; init; }

    /// <summary>Shell executable. Empty means "use the platform default shell".</summary>
    public string? ShellPath { get; init; }

    /// <summary>Ignored: arguments always come from the matching shell profile.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    public string? Cwd { get; init; }

    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Windows only: run the WSL distro with this name (Cwd is then a Linux path).</summary>
    public string? WslDistro { get; init; }

    public int Cols { get; init; } = 80;

    public int Rows { get; init; } = 24;
}

/// <summary>Counters for the diagnostics overlay (same fields as the original PtyStats). Times in Unix ms.</summary>
public sealed record PtyStats(int Pid, long WriteCount, long LastWriteTime, long DataCount, long LastDataTime, long DataBytes);

public sealed record PtyInfo(string Id, int Pid, string ShellPath, string Cwd, int Cols, int Rows);
