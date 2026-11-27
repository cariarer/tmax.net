namespace Tmax.Core.Git;

// Mirrors src/shared/diff-types.ts (serialized camelCase; enums as their lowercase strings).

public sealed record DiffFile(string Path, string? OldPath, string Status, int Additions, int Deletions);

public sealed class DiffLine
{
    public required string Type { get; init; }
    public required string Content { get; init; }
    public int? OldLineNumber { get; set; }
    public int? NewLineNumber { get; set; }
}

public sealed record DiffHunk(string Header, int OldStart, int OldCount, int NewStart, int NewCount, List<DiffLine> Lines);

public sealed record FileDiff(string Path, string? OldPath, List<DiffHunk> Hunks);

public sealed record DiffSummary(int FilesChanged, int Additions, int Deletions);

public sealed record DiffResult(List<DiffFile> Files, List<FileDiff> Diffs, DiffSummary Summary, string CommitHash, string Branch);

public sealed record AnnotatedLine(int LineNumber, string Content, string Type);

public sealed record AnnotatedFile(string Path, List<AnnotatedLine> Lines);

public sealed record WorktreeInfo
{
    public string Path { get; set; } = "";
    public string Head { get; set; } = "";
    public string? Branch { get; set; }
    public bool IsWorktree { get; set; }
    public bool? Detached { get; set; }
    public bool? Bare { get; set; }
    public bool? Locked { get; set; }
    public bool? Prunable { get; set; }
    public double? CreatedAt { get; set; }
}

public sealed record RepoWorktrees(string GitRoot, List<WorktreeInfo> Worktrees, bool IsExpanded, string? Error = null);

public sealed record GitResult(bool Success, string? WorktreePath = null, string? Error = null);
