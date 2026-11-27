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

    public int Cols { get; init; } = 80;

    public int Rows { get; init; } = 24;
}

public sealed record PtyInfo(string Id, int Pid, string ShellPath, string Cwd, int Cols, int Rows);
