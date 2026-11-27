using System.Text.Json.Nodes;

namespace Tmax.Core.Config;

/// <summary>Persisted layout/session (tmax-session.json, same <c>{"session": …}</c> shape as before).</summary>
public sealed class SessionStore(string? path = null)
{
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
}
