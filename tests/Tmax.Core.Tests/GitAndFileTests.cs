using Tmax.Core.Files;
using Tmax.Core.Git;
using Tmax.Core.Platform;

namespace Tmax.Core.Tests;

public sealed class GitAndFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tmax-git-test-").FullName;
    private string Repo => Path.Combine(_root, "repo");

    public GitAndFileTests()
    {
        Directory.CreateDirectory(Repo);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "t@example.invalid");
        Git("config", "user.name", "t");
        Git("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(Repo, "a.txt"), "one\ntwo\nthree\n");
        Git("add", ".");
        Git("commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        TestDirs.Delete(_root);
    }

    private void Git(params string[] args) => ProcessRunner.RunAsync("git", args, Repo).GetAwaiter().GetResult();

    [Fact]
    public async Task Unstaged_diff_reports_changed_and_untracked_files_with_line_numbers()
    {
        File.WriteAllText(Path.Combine(Repo, "a.txt"), "one\nTWO\nthree\n");
        File.WriteAllText(Path.Combine(Repo, "new.txt"), "x");

        var result = await new GitDiffService().GetDiffAsync(Repo, "unstaged");

        Assert.Equal("main", result.Branch);
        Assert.Contains(result.Files, f => f is { Path: "a.txt", Status: "modified", Additions: 1, Deletions: 1 });
        Assert.Contains(result.Files, f => f is { Path: "new.txt", Status: "added" });
        var lines = Assert.Single(Assert.Single(result.Diffs).Hunks).Lines;
        Assert.Contains(lines, l => l is { Type: "removed", Content: "two", OldLineNumber: 2 });
        Assert.Contains(lines, l => l is { Type: "added", Content: "TWO", NewLineNumber: 2 });
    }

    [Fact]
    public async Task Annotated_file_rejects_path_traversal()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new GitDiffService().GetAnnotatedFileAsync(Repo, "../../etc/passwd", "unstaged"));
    }

    [Fact]
    public async Task Resolve_git_root_works_from_a_subdirectory()
    {
        var sub = Directory.CreateDirectory(Path.Combine(Repo, "sub")).FullName;
        var root = await GitDiffService.ResolveGitRootAsync(sub);
        // git reports the real path (/var -> /private/var on macOS), so compare the tail.
        Assert.Equal("repo", Path.GetFileName(root));
        Assert.True(Directory.Exists(Path.Combine(root, ".git")));
    }

    [Fact]
    public async Task Worktrees_can_be_created_listed_and_removed()
    {
        var created = await GitWorktreeService.CreateAsync(Repo, "feature/x", "main");
        Assert.True(created.Success, created.Error);
        Assert.EndsWith("repo-feature-x", created.WorktreePath);

        var list = await GitWorktreeService.ListAsync(Repo);
        Assert.Equal(2, list.Worktrees.Count);
        Assert.Contains(list.Worktrees, w => w is { Branch: "feature/x", IsWorktree: true });
        Assert.Contains("feature/x", await GitWorktreeService.GetBranchesAsync(Repo));

        Assert.True((await GitWorktreeService.DeleteAsync(Repo, created.WorktreePath!)).Success);
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("-b")]
    public async Task Option_like_branch_names_are_refused(string branch)
    {
        var result = await GitWorktreeService.CreateAsync(Repo, branch, "main");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task File_explorer_lists_reads_and_renames()
    {
        Directory.CreateDirectory(Path.Combine(Repo, "dir"));
        var entries = FileService.List(Repo, null);
        Assert.Equal("dir", entries.First(e => !e.Name.StartsWith('.')).Name); // directories first
        Assert.Equal("one\ntwo\nthree\n", await FileService.ReadAsync(Path.Combine(Repo, "a.txt"), null));

        Assert.False(FileService.Rename(Path.Combine(Repo, "a.txt"), "../b.txt", null).Ok);
        var renamed = FileService.Rename(Path.Combine(Repo, "a.txt"), "b.txt", null);
        Assert.True(renamed.Ok);
        Assert.True(File.Exists(renamed.NewPath));
    }

    [Fact]
    public async Task Binary_and_invalid_wsl_paths_are_not_read()
    {
        var bin = Path.Combine(Repo, "bin.dat");
        File.WriteAllBytes(bin, [1, 0, 2]);
        Assert.Null(await FileService.ReadAsync(bin, null));
        Assert.Null(await FileService.ReadAsync("/etc/hosts", "bad;name"));
    }

    [Fact]
    public void Path_guard_rejects_sibling_prefix()
    {
        Assert.True(PathGuard.IsWithin("/home/user", "/home/user/x"));
        Assert.False(PathGuard.IsWithin("/home/user", "/home/user-evil/x"));
    }
}
