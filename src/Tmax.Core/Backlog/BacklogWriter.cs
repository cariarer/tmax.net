using System.Text.RegularExpressions;

namespace Tmax.Core.Backlog;

/// <summary>
/// Native Backlog.md writes without the CLI (port of src/main/backlog-writer.ts). Output stays
/// byte-compatible with backlog.md: id TASK-n, file "task-n - slug.md", updated_date after
/// created_date, titles quoted only when YAML needs it, AC lines "- [ ] #n text".
/// </summary>
public static partial class BacklogWriter
{
    private static readonly string[] WriteSubdirs = ["tasks", "completed"];
    private static readonly string[] AllSubdirs = ["tasks", "completed", "drafts", Path.Combine("archive", "tasks")];

    public static string BareId(string taskId) => Digits().Match(taskId) is { Success: true } m ? m.Value : taskId;

    private static string NowStamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    public static string YamlScalar(string s)
    {
        if (s.Length == 0) return "''";
        const string indicators = "-?:,[]{}#&*!|>'\"%@`";
        var needs = indicators.Contains(s[0]) || ColonSpace().IsMatch(s) || s.EndsWith(':') || SpaceHash().IsMatch(s)
                    || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]) || YamlKeyword().IsMatch(s) || Numeric().IsMatch(s);
        return needs ? $"'{s.Replace("'", "''")}'" : s;
    }

    public static string Slugify(string title) =>
        EdgeHyphen().Replace(Hyphens().Replace(NonSlug().Replace(title, "-"), "-"), "");

    public readonly record struct TaskLocation(string Dir, string File, string Full);

    private static TaskLocation? Find(string projectPath, string taskId, IEnumerable<string> subdirs)
    {
        var n = BareId(taskId);
        var pattern = new Regex($@"^task-{Regex.Escape(n)}\s+-\s");
        foreach (var sub in subdirs)
        {
            var dir = Path.Combine(projectPath, "backlog", sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var full in Directory.EnumerateFiles(dir))
            {
                var file = Path.GetFileName(full);
                if (pattern.IsMatch(file) || file == $"task-{n}.md") return new TaskLocation(dir, file, full);
            }
        }
        return null;
    }

    public static TaskLocation? LocateAnywhere(string projectPath, string taskId) => Find(projectPath, taskId, AllSubdirs);

    public static BacklogResult Edit(EditPayload p)
    {
        var loc = LocateAnywhere(p.ProjectPath, p.TaskId);
        if (loc is null) return new(false, $"Task {p.TaskId} not found");
        string content;
        try { content = File.ReadAllText(loc.Value.Full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, ex.Message); }

        var eol = content.Contains("\r\n") ? "\r\n" : "\n";
        var fmMatch = FrontmatterParts().Match(content);
        if (!fmMatch.Success) return new(false, "No frontmatter");
        var fm = fmMatch.Groups[2].Value;
        var body = content[fmMatch.Length..];
        var changed = false;

        if (!string.IsNullOrEmpty(p.Status))
        {
            fm = StatusLine().Replace(fm, "status: " + YamlScalar(p.Status).Replace("$", "$$"), 1);
            changed = true;
        }
        if (!string.IsNullOrEmpty(p.Title))
        {
            fm = TitleLine().Replace(fm, "title: " + YamlScalar(p.Title).Replace("$", "$$"), 1);
            changed = true;
        }

        var ops = (p.CheckAc ?? []).Select(i => (Idx: i, Check: true)).Concat((p.UncheckAc ?? []).Select(i => (Idx: i, Check: false))).ToList();
        if (ops.Count > 0)
        {
            var lines = LineSplit().Split(body);
            var inAc = false;
            var acNo = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (AcBegin().IsMatch(lines[i])) { inAc = true; continue; }
                if (AcEnd().IsMatch(lines[i])) { inAc = false; continue; }
                if (!inAc) continue;
                var m = Checkbox().Match(lines[i]);
                if (!m.Success) continue;
                acNo++;
                var op = ops.FirstOrDefault(o => o.Idx == acNo);
                if (op != default) lines[i] = $"{m.Groups[1].Value}[{(op.Check ? 'x' : ' ')}]{m.Groups[3].Value}";
            }
            body = string.Join(eol, lines);
            changed = true;
        }

        if (p.Description is not null)
        {
            var section = DescriptionSection().Match(body);
            body = section.Success
                ? body[..section.Groups[2].Index] + p.Description + body[(section.Groups[2].Index + section.Groups[2].Length)..]
                : body.TrimEnd() + $"\n\n## Description\n\n<!-- SECTION:DESCRIPTION:BEGIN -->\n{p.Description}\n<!-- SECTION:DESCRIPTION:END -->\n";
            changed = true;
        }

        if (!changed) return new(true);

        var stamp = NowStamp();
        if (UpdatedLine().IsMatch(fm)) fm = UpdatedLine().Replace(fm, $"updated_date: '{stamp}'", 1);
        else if (CreatedLine().IsMatch(fm)) fm = CreatedLine().Replace(fm, m => $"{m.Value}\nupdated_date: '{stamp}'", 1);

        var rebuilt = fmMatch.Groups[1].Value + fm + fmMatch.Groups[3].Value + body;
        var output = eol == "\r\n" ? AnyNewline().Replace(rebuilt, "\r\n") : rebuilt.Replace("\r\n", "\n");

        try
        {
            File.WriteAllText(loc.Value.Full, output);
            // A new title renames the file; a real status on an archived task moves it back to tasks/.
            var destName = !string.IsNullOrEmpty(p.Title) ? $"task-{BareId(p.TaskId)} - {Slugify(p.Title)}.md" : loc.Value.File;
            var archived = loc.Value.Dir.Replace('\\', '/').Contains("/archive/tasks");
            var unarchiving = archived && !string.IsNullOrEmpty(p.Status) && p.Status != "Archived";
            var destDir = unarchiving ? Path.Combine(p.ProjectPath, "backlog", "tasks") : loc.Value.Dir;
            var destFull = Path.Combine(destDir, destName);
            if (destFull != loc.Value.Full)
            {
                Directory.CreateDirectory(destDir);
                if (!File.Exists(destFull)) File.Move(loc.Value.Full, destFull);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
        return new(true);
    }

    private static int ReadZeroPad(string projectPath)
    {
        try
        {
            var m = ZeroPad().Match(File.ReadAllText(Path.Combine(projectPath, "backlog", "config.yml")));
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Every id on disk; allocation from the filesystem can't reuse a number (self-healing).</summary>
    private static HashSet<int> UsedIds(string projectPath)
    {
        var used = new HashSet<int>();
        foreach (var sub in AllSubdirs)
        {
            var dir = Path.Combine(projectPath, "backlog", sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir).Select(Path.GetFileName))
                if (TaskNumber().Match(f!) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n)) used.Add(n);
        }
        return used;
    }

    public static BacklogResult Create(CreatePayload p)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p.Title)) return new(false, "Title is required");
            var pad = ReadZeroPad(p.ProjectPath);
            var labels = p.Labels is { Length: > 0 } ? "\n" + string.Join('\n', p.Labels.Select(l => $"  - {YamlScalar(l)}")) : " []";
            string Content(string idNum) => string.Join('\n',
                "---", $"id: TASK-{idNum}", $"title: {YamlScalar(p.Title)}", $"status: {YamlScalar(p.Status ?? "To Do")}",
                "assignee: []", $"created_date: '{NowStamp()}'", $"labels:{labels}", "dependencies: []", "---", "",
                "## Description", "", "<!-- SECTION:DESCRIPTION:BEGIN -->", p.Description ?? "", "<!-- SECTION:DESCRIPTION:END -->", "");

            var used = UsedIds(p.ProjectPath);
            for (var attempt = 0; attempt < 25; attempt++)
            {
                var num = 1;
                foreach (var id in used) num = Math.Max(num, id + 1);
                while (used.Contains(num)) num++;
                var idNum = pad > 0 ? num.ToString().PadLeft(pad, '0') : num.ToString();
                var full = Path.Combine(p.ProjectPath, "backlog", "tasks", $"task-{idNum} - {Slugify(p.Title)}.md");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                try
                {
                    // CreateNew = exclusive: a concurrent create that took this id makes us pick the next.
                    using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
                    using var w = new StreamWriter(fs);
                    w.Write(Content(idNum));
                    return new(true, Id: $"TASK-{idNum}");
                }
                catch (IOException) when (File.Exists(full))
                {
                    used.Add(num);
                }
            }
            return new(false, "Could not allocate a unique task id");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
    }

    public static BacklogResult Archive(string projectPath, string taskId)
    {
        var loc = Find(projectPath, taskId, WriteSubdirs);
        if (loc is null) return new(false, $"Task {taskId} not found");
        try
        {
            var dest = Path.Combine(projectPath, "backlog", "archive", "tasks");
            Directory.CreateDirectory(dest);
            File.Move(loc.Value.Full, Path.Combine(dest, loc.Value.File));
            return new(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
    }

    public static BacklogResult InitProject(string projectPath, string? name)
    {
        try
        {
            var root = Path.Combine(projectPath, "backlog");
            foreach (var sub in new[] { "tasks", "completed", "drafts", "docs", "decisions", "milestones", Path.Combine("archive", "tasks") })
                Directory.CreateDirectory(Path.Combine(root, sub));
            var cfg = Path.Combine(root, "config.yml");
            if (!File.Exists(cfg))
            {
                var project = string.IsNullOrEmpty(name) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath)) : name;
                File.WriteAllText(cfg, string.Join('\n',
                    $"project_name: \"{project.Replace("\"", "'")}\"", "default_status: \"To Do\"", "statuses: [\"To Do\", \"In Progress\", \"Done\"]",
                    "labels: []", "milestones: []", "date_format: yyyy-mm-dd", "max_column_width: 20", "auto_open_browser: true",
                    "default_port: 6420", "auto_commit: false", "task_prefix: \"task\"", ""));
            }
            return new(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
    }

    [GeneratedRegex(@"\d+")] private static partial Regex Digits();
    [GeneratedRegex(@":\s")] private static partial Regex ColonSpace();
    [GeneratedRegex(@"\s#")] private static partial Regex SpaceHash();
    [GeneratedRegex("^(true|false|null|yes|no|on|off|~)$", RegexOptions.IgnoreCase)] private static partial Regex YamlKeyword();
    [GeneratedRegex(@"^[-+]?[0-9.]+$")] private static partial Regex Numeric();
    [GeneratedRegex("[^a-zA-Z0-9-]+")] private static partial Regex NonSlug();
    [GeneratedRegex("-+")] private static partial Regex Hyphens();
    [GeneratedRegex("^-|-$")] private static partial Regex EdgeHyphen();
    [GeneratedRegex(@"\A(---\r?\n)([\s\S]*?)(\r?\n---)")] private static partial Regex FrontmatterParts();
    // [^\r\n]* instead of .* so a CRLF file's \r is not swallowed (.NET's $ in multiline only matches before \n).
    [GeneratedRegex(@"^status:[^\r\n]*", RegexOptions.Multiline)] private static partial Regex StatusLine();
    [GeneratedRegex(@"^title:[^\r\n]*", RegexOptions.Multiline)] private static partial Regex TitleLine();
    [GeneratedRegex(@"^updated_date:[^\r\n]*", RegexOptions.Multiline)] private static partial Regex UpdatedLine();
    [GeneratedRegex(@"^created_date:[^\r\n]*", RegexOptions.Multiline)] private static partial Regex CreatedLine();
    [GeneratedRegex(@"\r?\n")] private static partial Regex LineSplit();
    [GeneratedRegex(@"\r?\n")] private static partial Regex AnyNewline();
    [GeneratedRegex(@"<!--\s*AC:BEGIN\s*-->")] private static partial Regex AcBegin();
    [GeneratedRegex(@"<!--\s*AC:END\s*-->")] private static partial Regex AcEnd();
    [GeneratedRegex(@"^(\s*-\s*)\[([ xX])\](.*)$")] private static partial Regex Checkbox();
    [GeneratedRegex(@"(<!--\s*SECTION:DESCRIPTION:BEGIN\s*-->\r?\n)([\s\S]*?)(\r?\n<!--\s*SECTION:DESCRIPTION:END\s*-->)")] private static partial Regex DescriptionSection();
    [GeneratedRegex(@"^zero_padded_ids:\s*(\d+)", RegexOptions.Multiline)] private static partial Regex ZeroPad();
    [GeneratedRegex(@"^task-(\d+)\b")] private static partial Regex TaskNumber();
}
