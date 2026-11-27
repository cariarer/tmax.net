using System.Globalization;
using System.Text.Json;

namespace Tmax.Core.AiSessions;

internal static class Json
{
    public static double ParseTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return 0;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds() : 0;
    }

    public static string? Str(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement? Obj(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Reads only complete lines appended since <paramref name="offset"/>; returns the new offset.</summary>
    public static (List<string> Lines, long NewOffset) ReadNewLines(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset >= fs.Length) return ([], offset);
        fs.Position = offset;
        var buffer = new byte[fs.Length - offset];
        var read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        // Stop at the last newline: a half-written line is parsed on the next round, not lost.
        var last = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
        if (last < 0) return ([], offset);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, last + 1);
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        return (lines, offset + last + 1);
    }

    public static IEnumerable<string> ReadAllLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
            if (line.Trim().Length > 0) yield return line;
    }

    public static JsonDocument? TryParse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }
}
