namespace Tmax.App.Rpc;

/// <summary>
/// Channels of the original app whose backend service is not ported yet.
/// Each answers with what the renderer treats as "nothing there" (empty list, null,
/// { ok: false }), so the UI runs without errors. Remove an entry when its service
/// is ported; the RpcServer refuses duplicate registrations, which makes forgotten
/// entries fail loudly at startup.
/// </summary>
public static class PendingChannels
{
    private const string NotYet = "Not available in tmax.net yet";

    public static void Register(RpcServer rpc)
    {
        var emptyList = Array.Empty<object>();
        var notOk = new { ok = false, error = NotYet };

        Value(rpc, emptyList,
            // AI session monitors
            "copilot:listSessions", "copilot:searchSessions", "copilot:getPrompts", "copilot:searchPrompts",
            "copilot:getRecentPrompts", "claude-code:listSessions", "claude-code:searchSessions",
            "claude-code:getPrompts",
            // process tree, fonts
"system:getFonts",
            // files, git, backlog
            "backlog:listTasks");

        Value(rpc, null,
            "copilot:getSession", "copilot:startWatching", "copilot:stopWatching", "claude-code:getSession",
            "claude-code:startWatching", "claude-code:stopWatching", "ai:invalidateCaches", "ai:getSessionTimeline",
            "version:getUpdate", "version:checkNow", "version:restartAndUpdate",
            "transparency:setMaterial",
            "backlog:getTask", "backlog:pickFolder",
            "detach:close", "detach:focus", "backlogWindow:close");

        Value(rpc, "", "version:getChangelog", "diag:getLogPath", "diag:readTail");
        Value(rpc, false, "transparency:platformSupports");

        Value(rpc, notOk,
            "backlog:editTask", "backlog:createTask", "backlog:archiveTask", "backlog:deleteTask",
            "backlog:validateProject", "backlog:initProject", "backlog:saveImage");

        // Things the user explicitly triggered: fail visibly instead of silently doing nothing.
        Fail(rpc,
            "detach:create", "backlogWindow:open");
    }

    private static void Value(RpcServer rpc, object? value, params string[] channels)
    {
        foreach (var channel in channels)
            rpc.Handle(channel, (_, _) => Task.FromResult(value));
    }

    private static void Fail(RpcServer rpc, params string[] channels)
    {
        foreach (var channel in channels)
            rpc.Handle(channel, (_, _) => Task.FromException<object?>(new NotSupportedException($"{channel}: {NotYet}")));
    }
}
