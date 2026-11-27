using System.Diagnostics;
using System.Text;

namespace Tmax.Core.Platform;

public sealed class ProcessFailedException(string message, int exitCode, string stderr) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string Stderr { get; } = stderr;
}

/// <summary>Runs a child process without a shell (arguments are passed as a list, never parsed).</summary>
public static class ProcessRunner
{
    public static async Task<string> RunAsync(string file, IEnumerable<string> args, string? cwd = null,
        TimeSpan? timeout = null, Encoding? encoding = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(file)} timed out");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var cmd = $"{Path.GetFileName(file)} {string.Join(' ', psi.ArgumentList)}";
            throw new ProcessFailedException($"Command failed: {cmd}\n{error.Trim()}", process.ExitCode, error);
        }
        return output;
    }
}
