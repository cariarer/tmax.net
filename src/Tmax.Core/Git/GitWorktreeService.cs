using System.Text.RegularExpressions;
using Tmax.Core.Platform;

namespace Tmax.Core.Git;

/// <summary>Port of src/main/git-worktree-service.ts.</summary>
public static partial class GitWorktreeService
{
    public static List<WorktreeInfo> ParsePorcelain(string output, string gitRoot)
    {
        var list = new List<WorktreeInfo>();
        WorktreeInfo? current = null;
        var first = true;

        void Push()
        {
            if (current is null || current.Path.Length == 0) return;
            current.IsWorktree = !(first || string.Equals(current.Path, gitRoot, StringComparison.OrdinalIgnoreCase));
            first = false;
            list.Add(current);
            current = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("worktree ")) { Push(); current = new WorktreeInfo { Path = Git.NativePath(line["worktree ".Length..]) }; }
            else if (current is null) continue;
            else if (line.StartsWith("HEAD ")) current.Head = line["HEAD ".Length..];
            else if (line.StartsWith("branch ")) current.Branch = line["branch ".Length..].Replace("refs/heads/", "");
            else if (line == "detached") current.Detached = true;
            else if (line == "bare") current.Bare = true;
            else if (line.StartsWith("locked")) current.Locked = true;
            else if (line.StartsWith("prunable")) current.Prunable = true;
            else if (line.Length == 0) Push();
        }
        Push();
        return list;
    }

    public static async Task<RepoWorktrees> ListAsync(string cwd)
    {
        try
        {
            var root = await GitDiffService.GetGitRootAsync(cwd);
            var worktrees = ParsePorcelain(await Git.RunAsync(root, "worktree", "list", "--porcelain"), root);
            foreach (var wt in worktrees)
            {
                if (!Directory.Exists(wt.Path)) continue;
                var info = new DirectoryInfo(wt.Path);
                var created = info.CreationTimeUtc;
                // Linux may not expose a birth time; fall back to mtime like the original.
                if (created.Year < 1980) created = info.LastWriteTimeUtc;
                wt.CreatedAt = new DateTimeOffset(created).ToUnixTimeMilliseconds();
            }
            return new RepoWorktrees(root, worktrees, true);
        }
        catch (ProcessFailedException ex) when (ex.Stderr.Contains("not a git repository"))
        {
            return new RepoWorktrees(cwd, [], true, "Not a git repository");
        }
        catch (Exception ex)
        {
            return new RepoWorktrees(cwd, [], true, ex.Message);
        }
    }

    /// <summary>Creates a sibling directory "&lt;repo&gt;-&lt;branch&gt;" on a new branch.</summary>
    public static async Task<GitResult> CreateAsync(string repoPath, string branchName, string baseBranch)
    {
        try
        {
            Git.AssertRef(branchName, "branch name");
            Git.AssertRef(baseBranch, "base branch");
            var repoName = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
            var parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repoPath))!;
            var safe = Unsafe().Replace(branchName.Replace('/', '-'), "-");
            safe = DotRun().Replace(safe, "-");
            safe = EdgeDash().Replace(safe, "");
            if (safe.Length == 0) return new GitResult(false, Error: "Branch name is invalid after sanitization");

            var worktreePath = Path.Combine(parentDir, $"{repoName}-{safe}");
            if (!PathGuard.IsWithin(parentDir, worktreePath))
                return new GitResult(false, Error: "Invalid branch name: would escape parent directory");

            await Git.RunAsync(repoPath, "worktree", "add", "-b", branchName, worktreePath, baseBranch);
            return new GitResult(true, worktreePath);
        }
        catch (Exception ex)
        {
            return new GitResult(false, Error: ex.Message);
        }
    }

    public static async Task<GitResult> DeleteAsync(string repoPath, string worktreePath)
    {
        try
        {
            if (worktreePath.StartsWith('-')) throw new ArgumentException("Invalid worktree path");
            await Git.RunAsync(repoPath, "worktree", "remove", worktreePath, "--force");
            return new GitResult(true);
        }
        catch (Exception ex)
        {
            return new GitResult(false, Error: ex.Message);
        }
    }

    public static async Task<List<string>> GetBranchesAsync(string repoPath)
    {
        try
        {
            // "* " marks the current branch, "+ " one checked out in another worktree.
            return (await Git.RunAsync(repoPath, "branch", "--format=%(refname:short)")).Split('\n')
                .Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        }
        catch (Exception)
        {
            return ["main", "master"];
        }
    }

    [GeneratedRegex(@"[^a-zA-Z0-9.\-_@]")] private static partial Regex Unsafe();
    [GeneratedRegex(@"\.\.+")] private static partial Regex DotRun();
    [GeneratedRegex(@"^[-.]+|[-.]+$")] private static partial Regex EdgeDash();
}
