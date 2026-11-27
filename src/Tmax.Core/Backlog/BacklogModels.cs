namespace Tmax.Core.Backlog;

public sealed record BacklogProjectRef(string Name, string Path);

/// <summary>Mirrors src/shared/backlog-types.ts.</summary>
public sealed record BacklogTask(
    string Id, string Title, string Status, List<string> Assignee, List<string> Labels, string? Priority,
    string File, string Sub, BacklogProjectRef Project, double Mtime, string? Created_date, string? Updated_date);

public sealed record BacklogTaskDetail(Dictionary<string, object> Frontmatter, string Body);

public sealed record BacklogResult(bool Ok, string? Error = null, string? Id = null, string? RelPath = null);

public sealed record EditPayload(string ProjectPath, string TaskId, string? Status = null, string? Title = null,
    string? Description = null, int[]? CheckAc = null, int[]? UncheckAc = null);

public sealed record CreatePayload(string ProjectPath, string Title, string? Status = null, string? Description = null,
    string[]? Labels = null);
