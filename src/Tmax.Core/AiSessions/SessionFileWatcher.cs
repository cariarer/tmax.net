using System.Collections.Concurrent;

namespace Tmax.Core.AiSessions;

/// <summary>
/// Watches "&lt;base&gt;/&lt;dir&gt;/&lt;file&gt;" session files (shared by the Copilot and Claude Code
/// watchers of the original). Strategy as there:
///  - native recursive FileSystemWatcher (FSEvents/inotify/ReadDirectoryChangesW),
///  - plus a 1 s "hot poll" of files written in the last 5 minutes, because some writers
///    (and network/WSL shares) don't produce reliable change events,
///  - plus a 10 s sweep that re-activates known files written again after going cold,
///  - polling-only mode for WSL shares (//wsl.localhost/…), where native events don't arrive.
/// Events are raised on a background thread.
/// </summary>
public sealed class SessionFileWatcher : IDisposable
{
    private static readonly TimeSpan HotWindow = TimeSpan.FromMinutes(5);
    private const int HotPollMs = 1000;
    private const int SweepMs = 10_000;
    private const int SettleMs = 250; // like chokidar's awaitWriteFinish

    private readonly string _basePath;
    private readonly Func<string, bool> _isSessionFile; // file name (no dir) -> relevant?
    private readonly bool _pollingOnly;
    private readonly ConcurrentDictionary<string, DateTime> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _hot = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Timer> _settle = new(StringComparer.Ordinal);
    private FileSystemWatcher? _fsw;
    private Timer? _hotTimer, _sweepTimer;
    private int _started;

    public SessionFileWatcher(string basePath, Func<string, bool> isSessionFile)
    {
        _basePath = Path.TrimEndingDirectorySeparator(basePath);
        _isSessionFile = isSessionFile;
        var norm = _basePath.Replace('\\', '/').ToLowerInvariant();
        _pollingOnly = norm.StartsWith("//wsl.localhost/") || norm.StartsWith("//wsl$/");
    }

    public event Action<string>? Added;
    public event Action<string>? Changed;
    public event Action<string>? Removed;

    /// <summary>Called every 10 s (the monitors re-check sessions in a non-idle state).</summary>
    public event Action? StaleCheck;

    public string BasePath => _basePath;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        foreach (var (file, mtime) in Enumerate()) // seed: existing files are not "new"
        {
            _known[file] = mtime;
            if (DateTime.UtcNow - mtime < HotWindow) _hot[file] = mtime;
        }

        if (!_pollingOnly) StartNative();
        _hotTimer = new Timer(_ => Safe(_pollingOnly ? PollAll : PollHot), null, HotPollMs, HotPollMs);
        _sweepTimer = new Timer(_ => Safe(() => { if (!_pollingOnly) Sweep(); StaleCheck?.Invoke(); }), null, SweepMs, SweepMs);
    }

    private void StartNative()
    {
        if (!Directory.Exists(_basePath)) return; // created later: hot poll + sweep can't see it, PollAll on restart will
        try
        {
            var w = new FileSystemWatcher(_basePath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => OnNative(e.FullPath);
            w.Changed += (_, e) => OnNative(e.FullPath);
            w.Renamed += (_, e) => { OnNative(e.OldFullPath); OnNative(e.FullPath); };
            w.Deleted += (_, e) => OnNative(e.FullPath);
            w.Error += (_, _) => Safe(PollAll); // overflow: resync from disk
            w.EnableRaisingEvents = true;
            _fsw = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // No native events (e.g. inotify limit): the hot poll + sweep still catch active sessions.
        }
    }

    private void OnNative(string fullPath)
    {
        if (!IsRelevant(fullPath)) return;
        // Debounce bursts of writes per file. A timer that fired (and was disposed by Settled)
        // in between is replaced; FileSystemWatcher handlers must not throw.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var timer = _settle.GetOrAdd(fullPath, p => new Timer(_ => Safe(() => Settled(p)), null, Timeout.Infinite, Timeout.Infinite));
            try
            {
                timer.Change(SettleMs, Timeout.Infinite);
                return;
            }
            catch (ObjectDisposedException)
            {
                _settle.TryRemove(new KeyValuePair<string, Timer>(fullPath, timer));
            }
        }
    }

    private void Settled(string file)
    {
        if (_settle.TryRemove(file, out var t)) t.Dispose();
        var exists = File.Exists(file);
        if (!exists)
        {
            _hot.TryRemove(file, out _);
            if (_known.TryRemove(file, out _)) Removed?.Invoke(file);
            return;
        }
        var mtime = File.GetLastWriteTimeUtc(file);
        _hot[file] = mtime;
        if (_known.TryAdd(file, mtime)) Added?.Invoke(file);
        else
        {
            _known[file] = mtime;
            Changed?.Invoke(file);
        }
    }

    private void PollHot()
    {
        var cutoff = DateTime.UtcNow - HotWindow;
        foreach (var (file, last) in _hot)
        {
            if (!File.Exists(file)) { _hot.TryRemove(file, out _); continue; }
            var mtime = File.GetLastWriteTimeUtc(file);
            if (mtime > last)
            {
                _hot[file] = mtime;
                _known[file] = mtime;
                Changed?.Invoke(file);
            }
            else if (mtime < cutoff) _hot.TryRemove(file, out _);
        }
    }

    /// <summary>Known files written again after leaving the hot set (e.g. a resumed session).</summary>
    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - HotWindow;
        foreach (var file in _known.Keys)
        {
            if (!File.Exists(file)) continue;
            var mtime = File.GetLastWriteTimeUtc(file);
            if (mtime < cutoff) { _hot.TryRemove(file, out _); continue; }
            if (!_hot.TryGetValue(file, out var known) || mtime > known)
            {
                _hot[file] = mtime;
                Changed?.Invoke(file);
            }
        }
    }

    /// <summary>Full rescan: used as the polling mode for WSL shares and after watcher overflows.</summary>
    private void PollAll()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, mtime) in Enumerate())
        {
            seen.Add(file);
            if (_known.TryGetValue(file, out var known))
            {
                if (mtime > known) { _known[file] = mtime; Changed?.Invoke(file); }
            }
            else
            {
                _known[file] = mtime;
                Added?.Invoke(file);
            }
        }
        foreach (var file in _known.Keys.Where(f => !seen.Contains(f)).ToList())
            if (_known.TryRemove(file, out _)) Removed?.Invoke(file);
    }

    private IEnumerable<(string File, DateTime Mtime)> Enumerate()
    {
        if (!Directory.Exists(_basePath)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_basePath).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs)
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(dir).Where(f => _isSessionFile(Path.GetFileName(f))).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return (f, File.GetLastWriteTimeUtc(f));
        }
    }

    /// <summary>Exactly base/&lt;dir&gt;/&lt;session file&gt;.</summary>
    private bool IsRelevant(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        return dir is not null && string.Equals(Path.GetDirectoryName(dir), _basePath, StringComparison.Ordinal)
               && _isSessionFile(Path.GetFileName(fullPath));
    }

    /// <summary>Timer/watcher callbacks must never throw: an exception there would take down the process.</summary>
    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    /// <summary>Unexpected errors in callbacks (I/O errors are retried on the next round).</summary>
    public event Action<Exception>? Error;

    public void Dispose()
    {
        _fsw?.Dispose();
        _hotTimer?.Dispose();
        _sweepTimer?.Dispose();
        foreach (var t in _settle.Values) t.Dispose();
        _settle.Clear();
    }
}
