using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Tmax.Core.Config;

/// <summary>
/// Diagnostics log (port of src/main/diag-logger.ts): tmax-diag.log in the data dir, buffered writes
/// every 500 ms, truncated beyond 2 MB. Every string in a payload is redacted (home dir → ~,
/// other users' home paths and the user name → &lt;user&gt;) so the log can be shared.
/// </summary>
public sealed class DiagLog : IDisposable
{
    private const long MaxSize = 2 * 1024 * 1024;
    private readonly StringBuilder _buffer = new();
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly Regex? _userName;
    // Log file only (never HTML): keep <user>, umlauts etc. readable instead of \uXXXX.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex HomePaths = new(@"([/\\])(Users|home)([/\\])[^/\\]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DiagLog(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(AppPaths.DataDir, "tmax-diag.log");
        var user = Environment.UserName;
        if (user.Length > 2) _userName = new Regex($@"\b{Regex.Escape(user)}\b", RegexOptions.Compiled);
        _timer = new Timer(_ => Flush(), null, 500, 500);
    }

    public string Path { get; }

    public void Log(string evt, JsonNode? data = null)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {Clip(evt, 80)}{(data is null ? "" : " " + Redact(data)!.ToJsonString(Readable))}\n";
        lock (_lock) _buffer.Append(line);
    }

    public void Log(string evt, object data) => Log(evt, JsonSerializer.SerializeToNode(data));

    public string RedactString(string s)
    {
        if (_home.Length > 3) s = s.Replace(_home, "~");
        s = HomePaths.Replace(s, "$1$2$3<user>");
        return _userName?.Replace(s, "<user>") ?? s;
    }

    private JsonNode? Redact(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(RedactString(s)),
        JsonArray a => new JsonArray(a.Select(x => Redact(x)).ToArray()),
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Redact(kv.Value)))),
        _ => node?.DeepClone(),
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public void Flush()
    {
        string text;
        lock (_lock)
        {
            if (_buffer.Length == 0) return;
            text = _buffer.ToString();
            _buffer.Clear();
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxSize)
                File.WriteAllText(Path, $"--- log rotated at {DateTime.UtcNow:O} ---\n");
            File.AppendAllText(Path, text);
        }
        catch (IOException) { /* diagnostics must never break the app */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Last <paramref name="maxBytes"/> of the log, starting at a full line.</summary>
    public string ReadTail(int maxBytes = 25 * 1024)
    {
        Flush();
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, fs.Length - Math.Clamp(maxBytes, 1, 1024 * 1024));
            fs.Position = start;
            var buf = new byte[fs.Length - start];
            fs.ReadExactly(buf);
            var text = Encoding.UTF8.GetString(buf);
            if (start > 0 && text.IndexOf('\n') is var nl and >= 0) text = text[(nl + 1)..];
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}
