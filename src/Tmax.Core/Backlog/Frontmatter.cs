using System.Text;
using System.Text.RegularExpressions;

namespace Tmax.Core.Backlog;

/// <summary>
/// The small YAML subset Backlog.md task files use (port of parseTaskFrontmatter from the original,
/// itself from backlog-hub): scalars, quoted scalars, block lists and folded/literal block scalars.
/// Values are string or List&lt;string&gt;.
/// </summary>
public static partial class Frontmatter
{
    public static Dictionary<string, object>? Parse(string content)
    {
        var m = Block().Match(content);
        if (!m.Success) return null;
        var lines = LineSplit().Split(m.Groups[1].Value);
        var output = new Dictionary<string, object>();
        string? listKey = null;
        var listAcc = new List<string>();

        void FlushList()
        {
            if (listKey is null) return;
            output[listKey] = listAcc;
            listKey = null;
            listAcc = [];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var item = ListItem().Match(line);
            if (item.Success && listKey is not null)
            {
                listAcc.Add(Unquote(item.Groups[1].Value));
                continue;
            }
            FlushList();
            var kv = KeyValue().Match(line);
            if (!kv.Success) continue;
            var key = kv.Groups[1].Value;
            var raw = kv.Groups[2].Value;
            var block = BlockScalar().Match(raw);
            if (block.Success)
            {
                var style = block.Groups[1].Value;
                var chomp = block.Groups[2].Value;
                var contentLines = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                    contentLines.Add(lines[++i]);
                var indent = 0;
                foreach (var cl in contentLines)
                {
                    if (cl.Trim().Length == 0) continue;
                    indent = cl.Length - cl.TrimStart().Length;
                    break;
                }
                var stripped = contentLines.Select(cl => cl.Length >= indent ? cl[indent..] : cl.TrimStart()).ToList();
                string value;
                if (style == "|")
                {
                    value = string.Join('\n', stripped);
                }
                else
                {
                    var parts = new List<string>();
                    var buf = "";
                    foreach (var sl in stripped)
                    {
                        if (sl.Length == 0)
                        {
                            if (buf.Length > 0) { parts.Add(buf); buf = ""; }
                            parts.Add("");
                        }
                        else buf = buf.Length > 0 ? buf + " " + sl : sl;
                    }
                    if (buf.Length > 0) parts.Add(buf);
                    var sb = new StringBuilder();
                    foreach (var part in parts)
                    {
                        if (part.Length == 0) sb.Append('\n');
                        else
                        {
                            if (sb.Length > 0 && sb[^1] != '\n') sb.Append(' ');
                            sb.Append(part);
                        }
                    }
                    value = sb.ToString();
                }
                if (chomp != "+") value = value.TrimEnd('\n');
                output[key] = value;
            }
            else if (raw.Length == 0)
            {
                listKey = key;
                listAcc = [];
            }
            else if (raw.Trim() == "[]")
            {
                output[key] = new List<string>();
            }
            else
            {
                output[key] = Unquote(raw);
            }
        }
        FlushList();
        return output;
    }

    public static string Unquote(string v)
    {
        var t = v.Trim();
        if (t.Length >= 2 && ((t[0] == '\'' && t[^1] == '\'') || (t[0] == '"' && t[^1] == '"'))) return t[1..^1];
        return t;
    }

    public static List<string> AsList(object? v) => v switch
    {
        List<string> l => l.Where(s => s.Length > 0).ToList(),
        string s when s.Trim().Length > 0 => [s.Trim()],
        _ => [],
    };

    /// <summary>Content after the frontmatter block.</summary>
    public static string Body(string content) => StripBlock().Replace(content, "", 1);

    [GeneratedRegex(@"\A---\r?\n([\s\S]*?)\r?\n---")] private static partial Regex Block();
    [GeneratedRegex(@"\A---\r?\n[\s\S]*?\r?\n---\r?\n?")] private static partial Regex StripBlock();
    [GeneratedRegex(@"\r?\n")] private static partial Regex LineSplit();
    [GeneratedRegex(@"^\s+-\s*(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^([A-Za-z_][\w-]*)\s*:\s*(.*)$")] private static partial Regex KeyValue();
    [GeneratedRegex(@"^([>|])([-+]?)\s*$")] private static partial Regex BlockScalar();
}
