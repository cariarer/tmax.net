using System.Text;
using Porta.Pty;

namespace Tmax.Core.Pty;

/// <summary>One live pseudoterminal and the shell running in it.</summary>
public sealed class PtySession : IAsyncDisposable
{
    private readonly IPtyConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private int _exited;

    internal PtySession(PtyInfo info, IPtyConnection connection, Action<PtySession, string> onData, Action<PtySession, int?> onExit)
    {
        Info = info;
        _connection = connection;
        _connection.ProcessExited += (_, e) => RaiseExit(e.ExitCode, onExit);
        _readLoop = Task.Run(() => ReadLoopAsync(onData, onExit));
    }

    public PtyInfo Info { get; private set; }

    public string Id => Info.Id;

    public OutputBuffer Output { get; } = new();

    public bool HasExited => Volatile.Read(ref _exited) == 1;

    public async Task WriteAsync(string data, CancellationToken ct = default)
    {
        if (HasExited) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.WriterStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _connection.WriterStream.FlushAsync(ct).ConfigureAwait(false);
        }
        // The shell can exit between the HasExited check and the write. Writing to the closed
        // PTY then fails with EBADF, which .NET surfaces as UnauthorizedAccessException.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(int cols, int rows)
    {
        if (HasExited) return;
        _connection.Resize(cols, rows);
        Info = Info with { Cols = cols, Rows = rows };
    }

    public void Kill()
    {
        if (HasExited) return;
        try { _connection.Kill(); } catch (InvalidOperationException) { /* already gone */ }
    }

    private async Task ReadLoopAsync(Action<PtySession, string> onData, Action<PtySession, int?> onExit)
    {
        // Stateful decoder: a UTF-8 sequence split across two reads must not turn into U+FFFD.
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _connection.ReaderStream.ReadAsync(bytes, _cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count == 0) continue;
                var text = new string(chars, 0, count);
                Output.Append(text);
                onData(this, text);
            }
        }
        catch (OperationCanceledException) { }
        // PTY closed underneath us (exit or dispose). EBADF arrives as UnauthorizedAccessException.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        catch (ObjectDisposedException) { }

        // EOF can arrive before ProcessExited; whichever comes first reports the exit.
        RaiseExit(null, onExit);
    }

    private void RaiseExit(int? exitCode, Action<PtySession, int?> onExit)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1) return;
        if (exitCode is null)
        {
            try { exitCode = _connection.ExitCode; } catch (InvalidOperationException) { }
        }
        onExit(this, exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await _cts.CancelAsync().ConfigureAwait(false);
        _connection.Dispose();
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
