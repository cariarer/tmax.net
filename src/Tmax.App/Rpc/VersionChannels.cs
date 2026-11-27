using Tmax.App.Updates;

namespace Tmax.App.Rpc;

public static class VersionChannels
{
    public static void Register(RpcServer rpc, UpdateService updates)
    {
        rpc.Handle("version:getUpdate", (_, _) => Task.FromResult<object?>(updates.Info));
        rpc.Handle("version:checkNow", (_, _) => { _ = updates.CheckAsync(); return Task.CompletedTask; });
        rpc.Handle("version:restartAndUpdate", (_, _) => { updates.RestartAndUpdate(); return Task.CompletedTask; });
        // Local CHANGELOG.md shipped with the app (the original fetched upstream's from GitHub).
        rpc.Handle("version:getChangelog", (_, _) => Task.FromResult<object?>(UpdateService.ReadChangelog()));
        rpc.Handle("version:getAppVersion", (_, _) => Task.FromResult<object?>(updates.Current));
    }
}
