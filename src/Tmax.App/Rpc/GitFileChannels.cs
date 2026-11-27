using Tmax.Core.Files;
using Tmax.Core.Git;

namespace Tmax.App.Rpc;

/// <summary>Diff editor, git worktrees and file explorer channels.</summary>
public static class GitFileChannels
{
    public static void Register(RpcServer rpc)
    {
        var diff = new GitDiffService();

        rpc.Handle("diff:resolveGitRoot", async (a, _) => await GitDiffService.ResolveGitRootAsync(a.Get<string>(0)));
        rpc.Handle("diff:getCodeChanges", async (a, _) => await diff.GetCodeChangesAsync(a.Get<string>(0), a.Get<string>(1)));
        rpc.Handle("diff:getDiff", async (a, _) => await diff.GetDiffAsync(a.Get<string>(0), a.Get<string>(1)));
        rpc.Handle("diff:getAnnotatedFile", async (a, _) => await diff.GetAnnotatedFileAsync(a.Get<string>(0), a.Get<string>(1), a.Get<string>(2)));

        rpc.Handle("git:listWorktrees", async (a, _) => await GitWorktreeService.ListAsync(a.Get<string>(0)));
        rpc.Handle("git:createWorktree", async (a, _) => await GitWorktreeService.CreateAsync(a.Get<string>(0), a.Get<string>(1), a.Get<string>(2)));
        rpc.Handle("git:deleteWorktree", async (a, _) => await GitWorktreeService.DeleteAsync(a.Get<string>(0), a.Get<string>(1)));
        rpc.Handle("git:getBranches", async (a, _) => await GitWorktreeService.GetBranchesAsync(a.Get<string>(0)));

        rpc.Handle("file:list", (a, _) => Task.FromResult<object?>(FileService.List(a.Get<string>(0), a.Opt<string>(1))));
        rpc.Handle("file:read", async (a, _) => await FileService.ReadAsync(a.Get<string>(0), a.Opt<string>(1)));
        rpc.Handle("file:reveal", (a, _) => Task.FromResult<object?>(FileService.Reveal(a.Get<string>(0), a.Opt<string>(1))));
        rpc.Handle("file:rename", (a, _) => Task.FromResult<object?>(FileService.Rename(a.Get<string>(0), a.Get<string>(1), a.Opt<string>(2))));
        rpc.Handle("file:delete", async (a, _) => await FileService.TrashAsync(a.Get<string>(0), a.Opt<string>(1)));
    }
}
