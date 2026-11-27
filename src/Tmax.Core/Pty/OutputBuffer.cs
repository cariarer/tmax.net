using System.Text;

namespace Tmax.Core.Pty;

/// <summary>
/// Bounded scrollback of raw terminal output, kept in the backend.
/// The UI does its own rendering via xterm.js; this copy exists so that
/// non-UI consumers (later: the MCP server) can read what a pane printed
/// without asking the frontend. Thread-safe.
/// </summary>
public sealed class OutputBuffer(int capacityChars = 256 * 1024)
{
    private readonly StringBuilder _buffer = new();
    private readonly Lock _lock = new();

    /// <summary>Total characters ever appended (monotonic, survives trimming).</summary>
    public long TotalWritten { get; private set; }

    public void Append(string chunk)
    {
        lock (_lock)
        {
            _buffer.Append(chunk);
            TotalWritten += chunk.Length;
            var overflow = _buffer.Length - capacityChars;
            if (overflow > 0) _buffer.Remove(0, overflow);
        }
    }

    public string Snapshot()
    {
        lock (_lock) return _buffer.ToString();
    }
}
