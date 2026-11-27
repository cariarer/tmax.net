using System.Reflection;
using Tmax.App.Rpc;
using Velopack;
using Velopack.Sources;

namespace Tmax.App.Updates;

/// <summary>
/// Auto-update via Velopack from GitHub releases of the fork (replaces version-checker.ts).
/// The repository comes from the build (-p:TmaxUpdateRepo=https://github.com/owner/repo); without
/// it, or when not installed through a Velopack installer (dev builds), updates stay off.
/// Status values match the original UI: idle | checking | downloading | downloaded | available | error.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private readonly RpcServer _rpc;
    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Timer? _timer;
    private Velopack.UpdateInfo? _pending;
    private object _info;

    public UpdateService(RpcServer rpc, ILogger<UpdateService> logger)
    {
        _rpc = rpc;
        _logger = logger;
        Current = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        Repository = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "TmaxUpdateRepo")?.Value;
        if (!string.IsNullOrWhiteSpace(Repository))
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource(Repository, null, false, null), null, null);
                if (mgr.IsInstalled) _manager = mgr;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Updates disabled: {Reason}", ex.Message);
            }
        }
        _info = new { status = "idle", current = Current };
    }

    public string Current { get; }
    public string? Repository { get; }
    public bool Enabled => _manager is not null;
    public object Info => _info;

    public void Start()
    {
        if (!Enabled) return;
        _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(10), CheckInterval);
    }

    private void Set(string status, string? latest = null, string? error = null, string? notes = null)
    {
        _info = new { status, current = Current, latest, url = Repository is null ? null : $"{Repository.TrimEnd('/')}/releases", error, releaseNotes = notes };
        _rpc.Broadcast("version:updateStatus", _info);
    }

    /// <summary>Checks and, if there is a newer release, downloads it in the background (like the original on Windows).</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || !await _gate.WaitAsync(0)) return;
        try
        {
            Set("checking");
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Set("idle");
                return;
            }
            var latest = update.TargetFullRelease.Version.ToString();
            var notes = update.TargetFullRelease.NotesMarkdown;
            Set("downloading", latest, notes: notes);
            await _manager.DownloadUpdatesAsync(update, null, CancellationToken.None);
            _pending = update;
            Set("downloaded", latest, notes: notes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Update check failed: {Error}", ex.Message);
            Set("error", error: ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a downloaded update and restarts; the caller's process exits.</summary>
    public void RestartAndUpdate()
    {
        if (_manager is null || _pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease, []);
    }

    public static string ReadChangelog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void Dispose() => _timer?.Dispose();
}
