using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tmax.Core.Pty;

/// <summary>
/// External pane titles (port of src/main/pane-title-files.ts). Every PTY gets its own file whose
/// path is exported as TMAX_TITLE_FILE. Tools that cannot write escape sequences to the terminal
/// (e.g. commands run by an AI CLI with captured output) set the pane title by writing one line to
/// that file; an empty file clears it.
/// </summary>
public sealed partial class PaneTitleFiles : IDisposable
{
    public const string EnvVar = "TMAX_TITLE_FILE";
    public const int MaxTitleLength = 120;
    private const int MaxFileBytes = 16 * 1024;
    private const int ReadRetryMs = 100;
    private const int MaxReadRetries = 5;
    private const int WatchRestartMs = 1000;

    private readonly string _dir;
    private readonly Action<string, string> _onTitle;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _ids = [];
    private readonly Dictionary<string, string> _lastTitles = [];
    private readonly HashSet<string> _disabled = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _retries = new();
    private FileSystemWatcher? _watcher;
    private Timer? _watchRestart;
    private bool _disposed;

    /// <param name="dir">Directory of this process (each tmax process uses its own subdirectory).</param>
    /// <param name="onTitle">(paneId, title) on every change; "" clears.</param>
    public PaneTitleFiles(string dir, Action<string, string> onTitle)
    {
        _dir = dir;
        _onTitle = onTitle;
    }

    /// <summary>Per-process directory under <paramref name="root"/>, after removing the ones of dead processes.</summary>
    public static string PrepareProcessDir(string root, Func<int, bool>? isAlive = null)
    {
        isAlive ??= IsProcessAlive;
        if (Directory.Exists(root))
        {
            foreach (var entry in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(entry);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                if (pid != Environment.ProcessId && isAlive(pid)) continue;
                try { Directory.Delete(entry, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return Path.Combine(root, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public static string Sanitize(string raw)
    {
        var text = raw.TrimStart('﻿');
        var nl = text.IndexOfAny(['\r', '\n']);
        if (nl >= 0) text = text[..nl];
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim();
        if (clean.Length > MaxTitleLength)
        {
            clean = clean[..MaxTitleLength];
            // Don't leave half a surrogate pair at the cut.
            if (char.IsHighSurrogate(clean[^1])) clean = clean[..^1];
        }
        return clean.Trim();
    }

    /// <summary>Creates an empty title file for the pane and returns its path, or null if unavailable.</summary>
    public string? Register(string id)
    {
        if (!SafeId().IsMatch(id)) return null;
        try
        {
            lock (_lock)
            {
                EnsureWatcher();
                File.WriteAllText(PathFor(id), "");
                _ids.Add(id);
                _lastTitles[id] = "";
            }
            return PathFor(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Release(string id, bool notify = true)
    {
        bool hadTitle;
        lock (_lock)
        {
            if (!_ids.Remove(id)) return;
            hadTitle = _lastTitles.Remove(id, out var t) && t.Length > 0;
            _disabled.Remove(id);
            ClearRetry(id);
        }
        try { File.Delete(PathFor(id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (notify && hadTitle) _onTitle(id, "");
    }

    public string GetTitle(string id)
    {
        lock (_lock) return _lastTitles.GetValueOrDefault(id, "");
    }

    /// <summary>The user renamed the pane manually: ignore the file from now on.</summary>
    public void Disable(string id)
    {
        lock (_lock)
        {
            if (!_ids.Contains(id)) return;
            _disabled.Add(id);
            ClearRetry(id);
            _lastTitles[id] = "";
        }
        _onTitle(id, "");
    }

    /// <summary>Re-reads a pane's file and reports a change. Called by the watcher; public for tests.</summary>
    public void Refresh(string id, int attempt = 0)
    {
        lock (_lock)
        {
            if (!_ids.Contains(id) || _disabled.Contains(id)) return;
            ClearRetry(id);
        }

        string title;
        try
        {
            using var fs = new FileStream(PathFor(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[MaxFileBytes];
            var read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            title = Sanitize(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (FileNotFoundException)
        {
            title = "";
        }
        catch (DirectoryNotFoundException)
        {
            title = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The writer may still hold the file (e.g. a Windows sharing lock): keep the
            // current title and read again with backoff instead of clearing it.
            if (attempt < MaxReadRetries) ScheduleRetry(id, attempt + 1);
            return;
        }

        lock (_lock)
        {
            if (!_ids.Contains(id) || _disabled.Contains(id)) return;
            if (_lastTitles.GetValueOrDefault(id) == title) return;
            _lastTitles[id] = title;
        }
        _onTitle(id, title);
    }

    private void ScheduleRetry(string id, int attempt)
    {
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            ClearRetry(id);
            _retries[id] = cts;
        }
        _ = Task.Delay(ReadRetryMs * (1 << (attempt - 1)), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _retries.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, cts));
            Refresh(id, attempt);
        }, TaskScheduler.Default);
    }

    private void ClearRetry(string id)
    {
        if (_retries.TryRemove(id, out var cts)) cts.Cancel();
    }

    private string PathFor(string id) => Path.Combine(_dir, $"{id}.title");

    private void EnsureWatcher()
    {
        if (_watcher is not null || _disposed) return;
        Directory.CreateDirectory(_dir);
        // Watch the directory rather than single files so atomic write-and-rename updates are seen too.
        var watcher = new FileSystemWatcher(_dir, "*.title")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, e) => OnFileEvent(e.Name);
        watcher.Created += (_, e) => OnFileEvent(e.Name);
        watcher.Renamed += (_, e) => OnFileEvent(e.Name);
        watcher.Deleted += (_, e) => OnFileEvent(e.Name);
        watcher.Error += (_, _) => RestartWatcher();
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void OnFileEvent(string? name)
    {
        if (name is null || !name.EndsWith(".title", StringComparison.Ordinal))
        {
            RefreshAll();
            return;
        }
        Refresh(name[..^".title".Length]);
    }

    private void RefreshAll()
    {
        string[] ids;
        lock (_lock) ids = [.. _ids];
        foreach (var id in ids) Refresh(id);
    }

    // A failed watcher (e.g. buffer overflow, directory removed) stays dead: recreate it and
    // re-read every pane so changes made in the meantime are not lost.
    private void RestartWatcher()
    {
        lock (_lock)
        {
            _watcher?.Dispose();
            _watcher = null;
            if (_watchRestart is not null || _disposed) return;
            _watchRestart = new Timer(_ =>
            {
                var failed = false;
                lock (_lock)
                {
                    _watchRestart?.Dispose();
                    _watchRestart = null;
                    if (_ids.Count == 0 || _disposed) return;
                    try
                    {
                        EnsureWatcher();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        failed = true;
                    }
                }
                if (failed) RestartWatcher(); // try again later
                else RefreshAll(); // even if Register() recreated the watcher meanwhile
            }, null, WatchRestartMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        string[] ids;
        lock (_lock)
        {
            _disposed = true;
            _watchRestart?.Dispose();
            _watchRestart = null;
            _watcher?.Dispose();
            _watcher = null;
            ids = [.. _ids];
        }
        foreach (var id in ids) Release(id, notify: false);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SafeId();
}
