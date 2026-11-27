using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tmax.Core.Config;

public sealed record Keybinding(string Key, string Action);

/// <summary>
/// VS Code-style keybindings.json (port of src/main/keybindings-file.ts): a JSON array of
/// { key, action } with // comments and trailing commas allowed; hot-reloaded on save.
/// </summary>
public sealed class KeybindingsFile : IDisposable
{
    public const string FileName = "keybindings.json";

    public static readonly string[] Actions =
    [
        "createTerminal", "closeTerminal", "focusUp", "focusDown", "focusLeft", "focusRight", "moveUp", "moveDown",
        "moveLeft", "moveRight", "splitHorizontal", "splitVertical", "toggleFloat", "toggleFocusMode", "toggleBroadcast",
        "toggleTabBar", "toggleCopilotPanel", "commandPalette", "paneHints", "jumpToTerminal", "renamePane",
        "showPrompts", "searchPrompts", "hidePane", "zoomIn", "zoomOut", "zoomReset", "newWorkspace", "nextWorkspace",
        "prevWorkspace", "goToWorkspace1", "goToWorkspace2", "goToWorkspace3", "goToWorkspace4", "goToWorkspace5",
        "goToWorkspace6", "goToWorkspace7", "goToWorkspace8", "goToWorkspace9",
    ];

    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public KeybindingsFile(string dir) => Path = System.IO.Path.Combine(dir, FileName);

    public string Path { get; }

    /// <summary>Raised (debounced) with the parsed bindings after the file changed on disk.</summary>
    public event Action<List<Keybinding>>? Changed;

    public static List<Keybinding> Parse(string content, Action<string>? warn = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            warn?.Invoke($"keybindings.json: parse error - {ex.Message}. File ignored.");
            return [];
        }
        if (root is not JsonArray array)
        {
            warn?.Invoke("keybindings.json: top-level value must be a JSON array. File ignored.");
            return [];
        }

        var result = new List<Keybinding>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o) { warn?.Invoke($"keybindings.json: entry {i} is not an object - skipped."); continue; }
            var key = o["key"] is JsonValue k && k.TryGetValue<string>(out var ks) ? ks : null;
            var action = o["action"] is JsonValue a && a.TryGetValue<string>(out var @as) ? @as : null;
            if (string.IsNullOrEmpty(key)) { warn?.Invoke($"keybindings.json: entry {i} missing string \"key\" - skipped."); continue; }
            if (string.IsNullOrEmpty(action)) { warn?.Invoke($"keybindings.json: entry {i} missing string \"action\" - skipped."); continue; }
            result.Add(new Keybinding(key, action));
        }
        return result;
    }

    public static string Serialize(IEnumerable<Keybinding> bindings)
    {
        var sb = new StringBuilder();
        sb.Append("""
            // tmax keybindings
            //
            // Edits here take effect immediately - no app restart needed.
            // Schema: a JSON array of { "key": "<accelerator>", "action": "<actionId>" }.
            //
            // Modifiers: Ctrl, Shift, Alt, Meta. Examples: "Ctrl+T", "Ctrl+Shift+W",
            // "Shift+ArrowUp". On macOS, Meta is interpreted as Cmd; Ctrl in this file
            // matches Cmd on Mac via the existing isMac convention.
            //
            // To unbind a default, remove its entry. To bind multiple keys to the same
            // action, list multiple entries with the same "action" value.
            //
            // Available actions:

            """);
        foreach (var a in Actions.Order(StringComparer.Ordinal)) sb.Append("//   - ").Append(a).Append('\n');
        sb.Append("""
            //
            // Trailing commas and // line comments are allowed. Malformed entries are
            // ignored - the rest of the file still applies, so a typo never locks you
            // out of your shortcuts.


            """);
        sb.Append(JsonSerializer.Serialize(bindings, Indented)).Append('\n');
        return sb.ToString();
    }

    public List<Keybinding> Read(Action<string>? warn = null)
    {
        try
        {
            return File.Exists(Path) ? Parse(File.ReadAllText(Path), warn) : [];
        }
        catch (IOException ex)
        {
            warn?.Invoke($"keybindings.json: read failed - {ex.Message}");
            return [];
        }
    }

    /// <summary>Creates the file from <paramref name="defaults"/> when missing; returns its bindings.</summary>
    public List<Keybinding> Init(IEnumerable<Keybinding> defaults, Action<string>? warn = null)
    {
        if (!File.Exists(Path))
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, Serialize(defaults));
            }
            catch (IOException ex)
            {
                warn?.Invoke($"keybindings.json: write failed - {ex.Message}");
                return defaults.ToList();
            }
        }
        return Read(warn);
    }

    public List<Keybinding> ResetToDefaults(IEnumerable<Keybinding> defaults)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, Serialize(defaults));
        return Read();
    }

    /// <summary>Watches the directory (editors often save via temp file + rename), debounced 150 ms.</summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;
        try
        {
            var w = new FileSystemWatcher(System.IO.Path.GetDirectoryName(Path)!, FileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, _) => _debounce?.Change(150, Timeout.Infinite);
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (_, _) => _debounce?.Change(150, Timeout.Infinite);
            _debounce = new Timer(_ => Changed?.Invoke(Read()), null, Timeout.Infinite, Timeout.Infinite);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // Hot reload unavailable (network mount, …); everything else still works.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
