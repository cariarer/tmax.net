using Tmax.Core.Pty;

namespace Tmax.App.Rpc;

/// <summary>Wires the PTY service to the channel names of the original app (src/shared/ipc-channels.ts).</summary>
public static class PtyChannels
{
    public const string Create = "pty:create";
    public const string Data = "pty:data";
    public const string Write = "pty:write";
    public const string Resize = "pty:resize";
    public const string Kill = "pty:kill";
    public const string Exit = "pty:exit";

    public static void Register(RpcServer rpc, PtyManager pty)
    {
        pty.DataReceived += (id, data) => rpc.Broadcast(Data, id, data);
        pty.Exited += (id, code) => rpc.Broadcast(Exit, id, code);

        rpc.Handle(Create, async (args, ct) =>
        {
            var info = await pty.CreateAsync(args.Get<PtyCreateOptions>(0), ct);
            return new { id = info.Id, pid = info.Pid };
        });
        rpc.Handle(Write, (args, ct) => pty.WriteAsync(args.Get<string>(0), args.Get<string>(1), ct));
        rpc.Handle(Resize, (args, _) =>
        {
            pty.Resize(args.Get<string>(0), args.Get<int>(1), args.Get<int>(2));
            return Task.CompletedTask;
        });
        rpc.Handle(Kill, (args, _) =>
        {
            pty.Kill(args.Get<string>(0));
            return Task.CompletedTask;
        });
    }
}
