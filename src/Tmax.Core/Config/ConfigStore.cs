using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tmax.Core.Pty;

namespace Tmax.Core.Config;

/// <summary>
/// Replacement for the original electron-store based ConfigStore: one JSON file
/// (tmax-config.json), top-level keys, defaults filled in for missing keys.
/// The renderer owns the schema; the backend only interprets <c>shells</c>.
/// </summary>
public sealed partial class ConfigStore
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Lock _lock = new();
    private readonly JsonObject _data;

    public ConfigStore(string? path = null, JsonObject? defaults = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.DataDir, "tmax-config.json");
        Defaults = defaults ?? DefaultConfig.Create();

        _data = JsonFile.TryRead(Path) ?? new JsonObject();
        var changed = !File.Exists(Path);
        foreach (var (key, value) in Defaults)
        {
            if (_data.ContainsKey(key)) continue;
            _data[key] = value?.DeepClone();
            changed = true;
        }
        if (changed) Save();
    }

    public string Path { get; }

    public JsonObject Defaults { get; }

    /// <summary>Raised after a key was written (key, new value).</summary>
    public event Action<string, JsonNode?>? Changed;

    public JsonObject GetAll()
    {
        lock (_lock) return (JsonObject)_data.DeepClone();
    }

    public JsonNode? Get(string key)
    {
        lock (_lock) return _data[key]?.DeepClone();
    }

    public T? Get<T>(string key)
    {
        var node = Get(key);
        return node is null ? default : node.Deserialize<T>(Web);
    }

    public void Set(string key, JsonNode? value)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException($"Invalid config key: {key}");
        if (key == "shells") ValidateShells(value);

        lock (_lock)
        {
            _data[key] = value?.DeepClone();
            Save();
        }
        Changed?.Invoke(key, value);
    }

    /// <summary>Configured shells, the allow-list for spawning (same rule as the original PTY_CREATE).</summary>
    public IReadOnlyList<ShellProfile> Shells
    {
        get
        {
            try
            {
                return Get<List<ShellProfile>>("shells")?
                    .Where(s => !string.IsNullOrWhiteSpace(s.Path))
                    .Select(s => s with { Args = s.Args ?? [] })
                    .ToList() ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private static void ValidateShells(JsonNode? value)
    {
        if (value is not JsonArray shells) throw new ArgumentException("shells must be an array");
        foreach (var shell in shells)
        {
            // Empty paths are fine: "+ Add Shell" stores a placeholder before the user fills it in.
            if (shell is JsonObject o && o["path"] is { } p && p.GetValueKind() != JsonValueKind.String)
                throw new ArgumentException($"Invalid shell path type: {p.GetValueKind()}");
        }
    }

    private void Save() => JsonFile.WriteAtomic(Path, _data);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex KeyPattern();
}
