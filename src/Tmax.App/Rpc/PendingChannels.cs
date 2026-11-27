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
        Value(rpc, null, "transparency:setMaterial");
        Value(rpc, false, "transparency:platformSupports");
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
