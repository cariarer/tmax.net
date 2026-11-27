using System.Text.RegularExpressions;
using Tmax.Core.Platform;

namespace Tmax.Core.Git;

/// <summary>Port of src/main/git-diff-service.ts. Modes: "unstaged", "staged", "branch" (vs. merge-base with main).</summary>
public sealed partial class GitDiffService
{
    /// <summary>Git root from a terminal cwd; falls back to walking up for a .git entry.</summary>
    public static async Task<string> ResolveGitRootAsync(string cwd)
    {
        if (string.IsNullOrEmpty(cwd))
            throw new ArgumentException("No CWD provided — terminal may not have reported its directory yet.");

        try
        {
            return Git.NativePath(await Git.RunAsync(cwd, "rev-parse", "--show-toplevel"));
        }
        catch (Exception ex) when (ex is ProcessFailedException or System.ComponentModel.Win32Exception or DirectoryNotFoundException or TimeoutException)
        {
        }

        for (var dir = cwd; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var dotGit = Path.Combine(dir, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit)) return dir;
        }
        throw new InvalidOperationException($"No git repository found from: {cwd}");
    }

    public static async Task<string> GetGitRootAsync(string cwd) =>
        Git.NativePath(await Git.RunAsync(cwd, "rev-parse", "--show-toplevel"));

    private static async Task<string> MergeBaseAsync(string root) =>
        (await Git.RunAsync(root, "merge-base", "HEAD", "main")).Trim();

    public async Task<List<DiffFile>> GetCodeChangesAsync(string cwd, string mode)
    {
        var root = await GetGitRootAsync(cwd);
        if (mode == "branch")
        {
            var mb = await MergeBaseAsync(root);
            return ParseFileList(await Git.RunAsync(root, "diff", "--numstat", $"{mb}..HEAD"),
                await Git.RunAsync(root, "diff", "--name-status", $"{mb}..HEAD"));
        }

        var staged = mode == "staged";
        var status = await Git.RunAsync(root, "status", "--porcelain", "-u");
        var numstat = await Git.RunAsync(root, staged ? ["diff", "--cached", "--numstat"] : ["diff", "--numstat"]);
        var nameStatus = await Git.RunAsync(root, staged ? ["diff", "--cached", "--name-status"] : ["diff", "--name-status"]);
        var files = ParseFileList(numstat, nameStatus);

        if (!staged)
        {
            foreach (var line in Lines(status))
            {
                if (line.Length < 4 || !line.StartsWith("??")) continue;
                var path = line[3..].Trim();
                if (files.All(f => f.Path != path)) files.Add(new DiffFile(path, null, "added", 0, 0));
            }
        }
        return files;
    }

    public static List<DiffFile> ParseFileList(string numstat, string nameStatus)
    {
        var statusMap = new Dictionary<string, string>();
        foreach (var line in Lines(nameStatus))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            var code = parts[0][0];
            var path = parts.Length > 2 ? parts[2] : parts[1];
            statusMap[path] = code switch { 'A' => "added", 'D' => "deleted", 'R' => "renamed", _ => "modified" };
        }

        var files = new List<DiffFile>();
        foreach (var line in Lines(numstat))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var path = string.Join('\t', parts[2..]);
            var isRename = path.Contains("=>");
            var actual = isRename ? RenameNew().Replace(path, "").Replace("{", "").Replace("}", "").Trim() : path;
            var old = isRename ? RenameOld().Replace(path, "").Replace("{", "").Replace("}", "").Trim() : null;
            files.Add(new DiffFile(actual, old, statusMap.GetValueOrDefault(actual, "modified"),
                parts[0] == "-" ? 0 : int.Parse(parts[0]), parts[1] == "-" ? 0 : int.Parse(parts[1])));
        }
        return files;
    }

    public async Task<DiffResult> GetDiffAsync(string cwd, string mode)
    {
        var root = await GetGitRootAsync(cwd);
        string[] args = mode switch
        {
            "branch" => ["diff", "-U3", $"{await MergeBaseAsync(root)}..HEAD"],
            "staged" => ["diff", "--cached", "-U3"],
            _ => ["diff", "-U3"],
        };
        var diffs = ParseDiff(await Git.RunAsync(root, args));
        var files = await GetCodeChangesAsync(cwd, mode);

        string hash = "", branch = "";
        try
        {
            hash = (await Git.RunAsync(root, "rev-parse", "--short", "HEAD")).Trim();
            branch = (await Git.RunAsync(root, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        }
        catch (ProcessFailedException) { /* no commits yet */ }

        return new DiffResult(files, diffs,
            new DiffSummary(files.Count, files.Sum(f => f.Additions), files.Sum(f => f.Deletions)), hash, branch);
    }

    public static List<FileDiff> ParseDiff(string raw)
    {
        var result = new List<FileDiff>();
        foreach (var chunk in DiffSplit().Split(raw).Where(c => c.Length > 0))
        {
            var lines = chunk.Split('\n');
            string path = "";
            string? oldPath = null;
            var header = DiffHeader().Match(lines[0]);
            if (header.Success)
            {
                oldPath = header.Groups[1].Value;
                path = header.Groups[2].Value;
                if (oldPath == path) oldPath = null;
            }

            var hunks = new List<DiffHunk>();
            DiffHunk? current = null;
            foreach (var line in lines)
            {
                var h = HunkHeader().Match(line);
                if (h.Success)
                {
                    current = new DiffHunk(line, int.Parse(h.Groups[1].Value),
                        h.Groups[2].Success ? int.Parse(h.Groups[2].Value) : 1,
                        int.Parse(h.Groups[3].Value), h.Groups[4].Success ? int.Parse(h.Groups[4].Value) : 1, []);
                    hunks.Add(current);
                    continue;
                }
                if (current is null) continue;
                if (line.StartsWith('+')) current.Lines.Add(new DiffLine { Type = "added", Content = line[1..] });
                else if (line.StartsWith('-')) current.Lines.Add(new DiffLine { Type = "removed", Content = line[1..] });
                else if (line.StartsWith(' ') || line.Length == 0)
                    current.Lines.Add(new DiffLine { Type = "unchanged", Content = line.Length > 0 ? line[1..] : line });
            }

            foreach (var hunk in hunks)
            {
                int oldLine = hunk.OldStart, newLine = hunk.NewStart;
                foreach (var l in hunk.Lines)
                {
                    if (l.Type == "added") l.NewLineNumber = newLine++;
                    else if (l.Type == "removed") l.OldLineNumber = oldLine++;
                    else { l.OldLineNumber = oldLine++; l.NewLineNumber = newLine++; }
                }
            }

            if (path.Length > 0) result.Add(new FileDiff(path, oldPath, hunks));
        }
        return result;
    }

    public async Task<AnnotatedFile> GetAnnotatedFileAsync(string cwd, string filePath, string mode)
    {
        var root = await GetGitRootAsync(cwd);
        var resolved = PathGuard.ResolveWithin(root, filePath); // reject traversal before touching git or disk
        string[] args = mode switch
        {
            "branch" => ["diff", "-U999999", $"{await MergeBaseAsync(root)}..HEAD", "--", filePath],
            "staged" => ["diff", "--cached", "-U999999", "--", filePath],
            _ => ["diff", "-U999999", "--", filePath],
        };
        var raw = await Git.RunAsync(root, args);

        var fileDiff = raw.Trim().Length > 0 ? ParseDiff(raw).FirstOrDefault() : null;
        if (fileDiff is null || fileDiff.Hunks.Count == 0)
        {
            // Unmodified or untracked: show the file as-is.
            try
            {
                var content = await File.ReadAllTextAsync(resolved);
                return new AnnotatedFile(filePath, content.Split('\n').Select((l, i) => new AnnotatedLine(i + 1, l, "unchanged")).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AnnotatedFile(filePath, []);
            }
        }

        var annotated = new List<AnnotatedLine>();
        var n = 1;
        foreach (var line in fileDiff.Hunks.SelectMany(h => h.Lines))
            annotated.Add(line.Type == "removed" ? new AnnotatedLine(-1, line.Content, "removed") : new AnnotatedLine(n++, line.Content, line.Type));
        return new AnnotatedFile(filePath, annotated);
    }

    private static IEnumerable<string> Lines(string s) => s.Split('\n').Where(l => l.Length > 0);

    [GeneratedRegex("^diff --git ", RegexOptions.Multiline)] private static partial Regex DiffSplit();
    [GeneratedRegex(@"a/(.+?) b/(.+)")] private static partial Regex DiffHeader();
    [GeneratedRegex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@(.*)")] private static partial Regex HunkHeader();
    [GeneratedRegex(".*=> ")] private static partial Regex RenameNew();
    [GeneratedRegex(" =>.*")] private static partial Regex RenameOld();
}
