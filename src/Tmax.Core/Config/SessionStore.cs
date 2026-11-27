using System.Text.Json.Nodes;

namespace Tmax.Core.Config;

/// <summary>Persisted layout/session (tmax-session.json, same <c>{"session": …}</c> shape as before).</summary>
public sealed class SessionStore(string? path = null) : IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly Lock _lock = new();

    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDir, "tmax-session.json");

    /// <summary>Always read from disk: another tmax.net window may have written it.</summary>
    public JsonNode? Load()
    {
        lock (_lock) return JsonFile.TryRead(Path)?["session"]?.DeepClone();
    }

    public void Save(JsonNode? session)
    {
        lock (_lock) JsonFile.WriteAtomic(Path, new JsonObject { ["session"] = session?.DeepClone() });
    }

    /// <summary>
    /// Reports every write to the file, including our own and other tmax.net instances' (TASK-163).
    /// The renderer ignores the echo of its own saves.
    /// </summary>
    public void Watch(Action onChanged)
    {
        if (_watcher is not null) return;
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        _debounce = new Timer(_ => onChanged(), null, Timeout.Infinite, Timeout.Infinite);
        // Watch the directory: the atomic write replaces the file via rename.
        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler h = (_, _) => _debounce.Change(100, Timeout.Infinite);
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Renamed += (_, _) => _debounce.Change(100, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
