using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Porta.Pty;

namespace Tmax.Core.Pty;

/// <summary>
/// Owns all PTY sessions. Transport-agnostic: the WebSocket bridge to the UI and
/// (later) the MCP server both talk to this class and subscribe to its events.
/// </summary>
public sealed partial class PtyManager : IAsyncDisposable
{
    // Vars Electron/Node inject or that could hijack spawned shells (from the original pty-manager.ts).
    private static readonly string[] EnvBlocklist =
    [
        "ELECTRON_RUN_AS_NODE", "ELECTRON_NO_ASAR", "ELECTRON_NO_ATTACH_CONSOLE", "ELECTRON_ENABLE_LOGGING",
        "ELECTRON_ENABLE_STACK_DUMPING", "ELECTRON_DEFAULT_ERROR_MODE", "ELECTRON_OVERRIDE_DIST_PATH",
        "NODE_OPTIONS", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        // Our own host process settings must not leak into user shells.
        "DOTNET_STARTUP_HOOKS", "ASPNETCORE_URLS", "ASPNETCORE_ENVIRONMENT",
    ];

    private readonly ConcurrentDictionary<string, PtySession> _sessions = new();
    private readonly IReadOnlyList<ShellProfile> _profiles;

    public PtyManager(IReadOnlyList<ShellProfile>? profiles = null)
    {
        _profiles = profiles ?? ShellProfiles.Detect();
    }

    /// <summary>Raised on a background thread for every output chunk.</summary>
    public event Action<string, string>? DataReceived;

    /// <summary>Raised once per session when its process exits.</summary>
    public event Action<string, int?>? Exited;

    public IReadOnlyList<ShellProfile> Profiles => _profiles;

    public IReadOnlyCollection<PtySession> Sessions => _sessions.Values.ToArray();

    public PtySession? Get(string id) => _sessions.GetValueOrDefault(id);

    public async Task<PtyInfo> CreateAsync(PtyCreateOptions opts, CancellationToken ct = default)
    {
        if (!IdPattern().IsMatch(opts.Id))
            throw new ArgumentException($"Invalid pane id: {opts.Id}");
        if (_sessions.ContainsKey(opts.Id))
            throw new InvalidOperationException($"Pane id already in use: {opts.Id}");

        var profile = ResolveProfile(opts.ShellPath);
        var cols = Math.Clamp(opts.Cols, 1, 500);
        var rows = Math.Clamp(opts.Rows, 1, 200);
        var cwd = ResolveCwd(opts.Cwd);

        var env = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "tmax",
            // Lets programs inside the pane (e.g. an agent using the future MCP server) identify their pane.
            ["TMAX_PANE_ID"] = opts.Id,
        };
        foreach (var key in EnvBlocklist) env[key] = ""; // empty value = remove (Porta.Pty semantics)
        if (opts.Env is not null)
        {
            foreach (var (key, value) in opts.Env)
            {
                if (EnvBlocklist.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                env[key] = value;
            }
        }

        var connection = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = opts.Id,
            App = profile.Path,
            CommandLine = (opts.Args ?? profile.Args).ToArray(),
            Cwd = cwd,
            Cols = cols,
            Rows = rows,
            Environment = env,
        }, ct).ConfigureAwait(false);

        var info = new PtyInfo(opts.Id, connection.Pid, profile.Path, cwd, cols, rows);
        var session = new PtySession(info, connection,
            onData: (s, data) => DataReceived?.Invoke(s.Id, data),
            onExit: (s, code) =>
            {
                _sessions.TryRemove(s.Id, out _);
                Exited?.Invoke(s.Id, code);
            });

        if (!_sessions.TryAdd(opts.Id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Pane id already in use: {opts.Id}");
        }
        return info;
    }

    public Task WriteAsync(string id, string data, CancellationToken ct = default) =>
        Get(id)?.WriteAsync(data, ct) ?? Task.CompletedTask;

    public void Resize(string id, int cols, int rows) =>
        Get(id)?.Resize(Math.Clamp(cols, 1, 500), Math.Clamp(rows, 1, 200));

    public void Kill(string id) => Get(id)?.Kill();

    private ShellProfile ResolveProfile(string? shellPath)
    {
        if (string.IsNullOrEmpty(shellPath))
            return _profiles.FirstOrDefault() ?? throw new InvalidOperationException("No shell found on this system.");

        return _profiles.FirstOrDefault(p => PathsEqual(p.Path, shellPath))
               ?? throw new UnauthorizedAccessException($"Shell path not in configured profiles: {shellPath}");
    }

    private static string ResolveCwd(string? cwd)
    {
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) return cwd;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{1,128}$")]
    private static partial Regex IdPattern();

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }
}
