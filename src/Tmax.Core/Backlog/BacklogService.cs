using Tmax.Core.Platform;

namespace Tmax.Core.Backlog;

/// <summary>Reads Backlog.md tasks straight from disk across projects (port of src/main/backlog-service.ts).</summary>
public static class BacklogService
{
    private static readonly string[] TaskSubdirs = ["tasks", "completed"];
    private static readonly string[] ReadSubdirs = ["tasks", "completed", "archive/tasks"];

    public static async Task<List<BacklogTask>> ListAllAsync(IEnumerable<BacklogProjectRef>? projects, bool includeArchived)
    {
        var perProject = await Task.WhenAll((projects ?? []).Where(p => !string.IsNullOrEmpty(p?.Path))
            .Select(p => ScanProjectAsync(p, includeArchived)));
        return perProject.SelectMany(t => t).ToList();
    }

    private static async Task<List<BacklogTask>> ScanProjectAsync(BacklogProjectRef project, bool includeArchived)
    {
        var tasks = new List<BacklogTask>();
        foreach (var sub in includeArchived ? ReadSubdirs : TaskSubdirs)
        {
            var dir = Path.Combine(project.Path, "backlog", sub);
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.md"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var parsed = await Task.WhenAll(files.Select(async full =>
            {
                try
                {
                    var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
                    var fm = Frontmatter.Parse(await File.ReadAllTextAsync(full));
                    if (fm is null) return null;
                    var file = Path.GetFileName(full);
                    var name = file[..^3];
                    // Archived tasks get a synthetic column; files in completed/ are Done even if the
                    // frontmatter predates the move.
                    var status = sub == "archive/tasks" ? "Archived" : Str(fm, "status") ?? (sub == "completed" ? "Done" : "To Do");
                    return new BacklogTask(Str(fm, "id") ?? name, Str(fm, "title") ?? name, status,
                        Frontmatter.AsList(fm.GetValueOrDefault("assignee")), Frontmatter.AsList(fm.GetValueOrDefault("labels")),
                        Str(fm, "priority"), file, sub, project, mtime, Str(fm, "created_date"), Str(fm, "updated_date"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }));
            tasks.AddRange(parsed.OfType<BacklogTask>());
        }
        return tasks;
    }

    private static string? Str(Dictionary<string, object> fm, string key) =>
        fm.GetValueOrDefault(key) is string s && s.Length > 0 ? s : null;

    public static BacklogTaskDetail? GetTask(string projectPath, string sub, string file)
    {
        // sub must be a known subdir and file a bare name (no traversal).
        if (!ReadSubdirs.Contains(sub) || !PathGuard.IsPlainName(file) || file.Contains("..")) return null;
        try
        {
            var content = File.ReadAllText(Path.Combine(projectPath, "backlog", sub, file));
            return new BacklogTaskDetail(Frontmatter.Parse(content) ?? [], Frontmatter.Body(content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsProject(string projectPath) =>
        !string.IsNullOrEmpty(projectPath) && Directory.Exists(Path.Combine(projectPath, "backlog"));

    /// <summary>Saves a pasted image into backlog/.attachments and returns the path relative to a task file.</summary>
    public static async Task<BacklogResult> SaveImageAsync(string projectPath, byte[] png)
    {
        try
        {
            if (!IsProject(projectPath)) return new(false, "Not a Backlog.md project");
            var dir = Path.Combine(projectPath, "backlog", ".attachments");
            Directory.CreateDirectory(dir);
            var name = $"img-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
            await File.WriteAllBytesAsync(Path.Combine(dir, name), png);
            return new(true, RelPath: $"../.attachments/{name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
    }

    public static async Task<BacklogResult> DeleteAsync(string projectPath, string taskId)
    {
        var loc = BacklogWriter.LocateAnywhere(projectPath, taskId);
        if (loc is null) return new(false, $"Task {taskId} not found");
        try
        {
            await OsShell.MoveToTrashAsync(loc.Value.Full); // recoverable, unlike archive it leaves the board
            return new(true);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }
}
