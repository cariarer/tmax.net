using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Porta.Pty;
using Tmax.Core.Platform;

namespace Tmax.Core.Pty;

/// <summary>
/// Owns all PTY sessions (port of src/main/pty-manager.ts plus the PTY_CREATE checks from main.ts).
/// Transport-agnostic: the WebSocket bridge to the UI and (later) the MCP server both use this class.
/// </summary>
public sealed partial class PtyManager : IAsyncDisposable
{
    // Vars that break child processes or could hijack spawned shells (original ELECTRON_ENV_BLOCKLIST).
    private static readonly HashSet<string> EnvBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "ELECTRON_RUN_AS_NODE", "ELECTRON_NO_ASAR", "ELECTRON_NO_ATTACH_CONSOLE", "ELECTRON_ENABLE_LOGGING",
        "ELECTRON_ENABLE_STACK_DUMPING", "ELECTRON_DEFAULT_ERROR_MODE", "ELECTRON_OVERRIDE_DIST_PATH",
        "GOOGLE_API_KEY", "GOOGLE_DEFAULT_CLIENT_ID", "GOOGLE_DEFAULT_CLIENT_SECRET", "ORIGINAL_XDG_CURRENT_DESKTOP",
        "NODE_OPTIONS", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "DYLD_FRAMEWORK_PATH", "NODE_EXTRA_CA_CERTS",
        // Our own host process settings must not leak into user shells.
        "DOTNET_STARTUP_HOOKS", "ASPNETCORE_URLS", "ASPNETCORE_ENVIRONMENT",
        // Set per pane below; never inherited from tmax's own environment.
        PaneTitleFiles.EnvVar, "TMAX_PANE_ID",
    };

    // Reports the cwd via OSC 7 after every prompt (used by explorer, diff, "new pane here").
    private const string PowerShellIntegration =
        "$Global:__tmax_origPrompt = $function:prompt\n" +
        "function Global:prompt {\n" +
        "  $p = & $Global:__tmax_origPrompt\n" +
        "  $d = $executionContext.SessionState.Path.CurrentLocation.Path\n" +
        "  $u = \"file:///\" + ($d -replace \"\\\\\",\"/\")\n" +
        "  $esc = [char]27\n" +
        "  $bel = [char]7\n" +
        "  [Console]::Write(\"${esc}]7;${u}${bel}\")\n" +
        "  return $p\n" +
        "}\n";

    // zsh has no PROMPT_COMMAND: add a precmd hook. It also fires chpwd once on the first prompt so
    // prompt themes that fill their cwd segment from chpwd show it although no `cd` happened.
    private const string ZshIntegration =
        "__tmax_precmd() { printf '\\e]7;file:///%s\\a' \"$PWD\"; if [[ -z \"$_TMAX_CHPWD_FIRED\" ]]; then _TMAX_CHPWD_FIRED=1; " +
        "(( ${+functions[chpwd]} )) && chpwd; [[ -n \"${chpwd_functions+x}\" ]] && for _f in \"${chpwd_functions[@]}\"; do " +
        "(( ${+functions[$_f]} )) && \"$_f\"; done; fi }; precmd_functions+=(__tmax_precmd)";

    private static readonly Lazy<string?> PowerShellScript = new(() =>
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "tmax-pwsh-integration.ps1");
            File.WriteAllText(path, PowerShellIntegration);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    });

    private readonly ConcurrentDictionary<string, PtySession> _sessions = new();
    private readonly Func<IReadOnlyList<ShellProfile>> _profiles;
    private readonly PaneTitleFiles? _titleFiles;

    /// <param name="profiles">Allowed shells. Read on every spawn, so config edits apply immediately.</param>
    /// <param name="titleFiles">Optional TMAX_TITLE_FILE support.</param>
    public PtyManager(Func<IReadOnlyList<ShellProfile>> profiles, PaneTitleFiles? titleFiles = null)
    {
        _profiles = profiles;
        _titleFiles = titleFiles;
    }

    public PtyManager(IReadOnlyList<ShellProfile>? profiles = null)
    {
        var fixedProfiles = profiles ?? ShellProfiles.Detect();
        _profiles = () => fixedProfiles;
    }

    /// <summary>Raised on a background thread for every output chunk.</summary>
    public event Action<string, string>? DataReceived;

    /// <summary>Raised once per session when its process exits.</summary>
    public event Action<string, int?>? Exited;

    public IReadOnlyList<ShellProfile> Profiles => _profiles();

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
        var shellName = Path.GetFileName(profile.Path).ToLowerInvariant();
        var isWsl = !string.IsNullOrEmpty(opts.WslDistro) || shellName is "wsl.exe" or "wsl";

        // Arguments come from the trusted profile, never from the frontend. For a WSL distro
        // the distro and its Linux cwd are passed to wsl.exe instead.
        List<string> args;
        string cwd;
        if (!string.IsNullOrEmpty(opts.WslDistro))
        {
            if (!PathGuard.IsValidWslDistro(opts.WslDistro)) throw new ArgumentException($"Invalid WSL distro name: {opts.WslDistro}");
            args = ["-d", opts.WslDistro];
            if (opts.Cwd?.StartsWith('/') == true) args.AddRange(["--cd", opts.Cwd]);
            cwd = HomeDir; // the Windows side still needs a valid directory
        }
        else
        {
            args = [.. profile.Args];
            cwd = ResolveCwd(opts.Cwd ?? profile.Cwd);
        }

        var env = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            // Like the original: AI CLIs that detect VS Code use their non-Ink renderer, which keeps
            // the conversation in the scrollback instead of redrawing it in place.
            ["TERM_PROGRAM"] = "vscode",
            ["TMAX_VERSION"] = "1",
            ["COLORTERM"] = "truecolor",
            // Lets programs inside the pane (e.g. an agent using the future MCP server) identify their pane.
            ["TMAX_PANE_ID"] = opts.Id,
        };
        foreach (var key in EnvBlocklist) env.TryAdd(key, ""); // empty value = remove (Porta.Pty semantics)
        foreach (var extra in new[] { profile.Env, opts.Env })
        {
            if (extra is null) continue;
            foreach (var (key, value) in extra)
                if (!EnvBlocklist.Contains(key)) env[key] = value;
        }

        var titleFile = _titleFiles?.Register(opts.Id);
        if (titleFile is not null)
        {
            env[PaneTitleFiles.EnvVar] = titleFile;
            if (OperatingSystem.IsWindows() && isWsl)
            {
                var entries = (Environment.GetEnvironmentVariable("WSLENV") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                    .Where(e => !e.StartsWith(PaneTitleFiles.EnvVar, StringComparison.OrdinalIgnoreCase));
                env["WSLENV"] = string.Join(':', entries.Append($"{PaneTitleFiles.EnvVar}/up"));
            }
        }

        if (!isWsl && shellName.StartsWith("bash"))
            env["PROMPT_COMMAND"] = "printf \"\\e]7;file:///%s\\a\" \"$(pwd)\"";

        var isPowerShell = shellName.StartsWith("pwsh") || shellName.StartsWith("powershell");
        var hasCustomCommand = args.Any(a => CustomCommandArg().IsMatch(a));
        if (isPowerShell && !hasCustomCommand && PowerShellScript.Value is { } script)
            args.AddRange(["-NoExit", "-Command", $". '{script.Replace("'", "''")}'"]);

        IPtyConnection connection;
        try
        {
            connection = await PtyProvider.SpawnAsync(new PtyOptions
            {
                Name = opts.Id,
                App = profile.Path,
                CommandLine = args.ToArray(),
                Cwd = cwd,
                Cols = cols,
                Rows = rows,
                Environment = env,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            _titleFiles?.Release(opts.Id);
            throw;
        }

        var info = new PtyInfo(opts.Id, connection.Pid, profile.Path, cwd, cols, rows);
        var session = new PtySession(info, connection,
            onData: (s, data) => DataReceived?.Invoke(s.Id, data),
            onExit: (s, code) =>
            {
                _sessions.TryRemove(s.Id, out _);
                _titleFiles?.Release(s.Id);
                Exited?.Invoke(s.Id, code);
            });

        if (!_sessions.TryAdd(opts.Id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Pane id already in use: {opts.Id}");
        }

        if (!isWsl && shellName.StartsWith("zsh"))
            _ = InjectZshIntegrationAsync(session);

        return info;
    }

    private static async Task InjectZshIntegrationAsync(PtySession session)
    {
        await Task.Delay(200).ConfigureAwait(false);
        await session.WriteAsync(ZshIntegration + "\r").ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        await session.WriteAsync("clear\r").ConfigureAwait(false);
    }

    public Task WriteAsync(string id, string data, CancellationToken ct = default) =>
        Get(id)?.WriteAsync(data, ct) ?? Task.CompletedTask;

    public void Resize(string id, int cols, int rows) =>
        Get(id)?.Resize(Math.Clamp(cols, 1, 500), Math.Clamp(rows, 1, 200));

    public void Kill(string id)
    {
        Get(id)?.Kill();
        _titleFiles?.Release(id);
    }

    public PtyStats? GetStats(string id) => Get(id)?.Stats;

    public string GetExternalTitle(string id) => _titleFiles?.GetTitle(id) ?? "";

    public void DisableExternalTitle(string id) => _titleFiles?.Disable(id);

    public async Task<List<string>> GetChildProcessNamesAsync(string id) =>
        Get(id) is { } s ? await ProcessTree.GetDescendantNamesAsync(s.Info.Pid).ConfigureAwait(false) : [];

    private ShellProfile ResolveProfile(string? shellPath)
    {
        var profiles = _profiles();
        if (string.IsNullOrEmpty(shellPath))
            return profiles.FirstOrDefault() ?? throw new InvalidOperationException("No shell found on this system.");

        return profiles.FirstOrDefault(p => PathsEqual(p.Path, shellPath))
               ?? throw new UnauthorizedAccessException($"Shell path not in configured profiles: {shellPath}");
    }

    private static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Existing directory; "~" / "~/x" expanded; anything else falls back to the home directory.</summary>
    public static string ResolveCwd(string? cwd)
    {
        if (!string.IsNullOrEmpty(cwd) && (cwd == "~" || cwd.StartsWith("~/") || cwd.StartsWith("~\\")))
            cwd = Path.Join(HomeDir, cwd[1..]);
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) return cwd;
        return HomeDir;
    }

    private static bool PathsEqual(string a, string b)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Bare names like "cmd.exe" / "wsl.exe" in the config are compared as written.
        if (!Path.IsPathRooted(a) || !Path.IsPathRooted(b)) return string.Equals(a, b, cmp);
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), cmp);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{1,128}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^-(Command|c|File|f|EncodedCommand)$", RegexOptions.IgnoreCase)]
    private static partial Regex CustomCommandArg();

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
            await session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
        _titleFiles?.Dispose();
    }
}
