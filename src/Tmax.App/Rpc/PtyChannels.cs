using System.Collections.Concurrent;
using System.Text;
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
    public const string ExternalTitle = "pty:externalTitle";

    public static void Register(RpcServer rpc, PtyManager pty)
    {
        var batcher = new OutputBatcher((id, data) => rpc.Broadcast(Data, id, data));
        pty.DataReceived += batcher.Add;
        pty.Exited += (id, code) =>
        {
            batcher.Remove(id); // last output goes out before the exit event
            rpc.Broadcast(Exit, id, code);
        };

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
        rpc.Handle("pty:getDiag", (args, _) => Task.FromResult<object?>(pty.GetStats(args.Get<string>(0))));
        rpc.Handle("pty:getChildProcesses", async (args, _) => await pty.GetChildProcessNamesAsync(args.Get<string>(0)));
        rpc.Handle("pty:getExternalTitle", (args, _) => Task.FromResult<object?>(pty.GetExternalTitle(args.Get<string>(0))));
        rpc.Handle("pty:disableExternalTitle", (args, _) =>
        {
            pty.DisableExternalTitle(args.Get<string>(0));
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Coalesces output per pane and flushes at most every 12 ms (as the original did), so bursts
    /// (e.g. `cat` of a large file, resume from sleep) become a few big frames instead of thousands.
    /// </summary>
    private sealed class OutputBatcher(Action<string, string> flush)
    {
        private const int IntervalMs = 12;
        private readonly ConcurrentDictionary<string, StringBuilder> _pending = new();
        private int _scheduled;

        public void Add(string id, string data)
        {
            var sb = _pending.GetOrAdd(id, _ => new StringBuilder());
            lock (sb) sb.Append(data);
            if (Interlocked.Exchange(ref _scheduled, 1) == 0)
                _ = Task.Delay(IntervalMs).ContinueWith(_ => FlushAll(), TaskScheduler.Default);
        }

        public void Flush(string id)
        {
            if (!_pending.TryGetValue(id, out var sb)) return;
            string text;
            lock (sb)
            {
                if (sb.Length == 0) return;
                text = sb.ToString();
                sb.Clear();
            }
            flush(id, text);
        }

        private void FlushAll()
        {
            Volatile.Write(ref _scheduled, 0);
            foreach (var id in _pending.Keys) Flush(id);
        }

        /// <summary>Flushes and forgets a pane (on exit; no output can follow).</summary>
        public void Remove(string id)
        {
            Flush(id);
            _pending.TryRemove(id, out _);
        }
    }
}
