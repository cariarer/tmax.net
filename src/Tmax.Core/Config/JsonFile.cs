using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tmax.Core.Config;

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonObject? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Write-to-temp + rename, so a crash mid-write never leaves a truncated file.</summary>
    public static void WriteAtomic(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, node.ToJsonString(Indented));
        File.Move(tmp, path, overwrite: true);
    }
}
