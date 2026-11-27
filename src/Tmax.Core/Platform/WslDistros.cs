using System.Text;

namespace Tmax.Core.Platform;

public sealed record WslDistro(string Name, string Home, string ClaudeBasePath, string CopilotBasePath);

/// <summary>Installed WSL distros and their home dirs (port of src/main/wsl-utils.ts). Windows only.</summary>
public static class WslDistros
{
    private static List<WslDistro>? _cache;

    public static string WslExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsl.exe");

    public static bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(WslExe);

    /// <summary>//wsl.localhost/&lt;distro&gt;/… (forward slashes work with .NET file APIs and match the renderer).</summary>
    public static string ToUnc(string distro, string linuxPath) => $"//wsl.localhost/{distro}{linuxPath}";

    public static async Task<List<WslDistro>> GetAsync()
    {
        if (_cache is not null) return _cache;
        if (!IsAvailable) return _cache = [];

        var result = new List<WslDistro>();
        string list;
        try
        {
            // wsl.exe -l prints UTF-16LE.
            list = await ProcessRunner.RunAsync(WslExe, ["-l", "-q"], timeout: TimeSpan.FromSeconds(10), encoding: Encoding.Unicode);
        }
        catch (Exception)
        {
            return [];
        }
        foreach (var name in list.Replace("\0", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Windows Subsystem")))
        {
            if (!PathGuard.IsValidWslDistro(name)) continue;
            try
            {
                var home = (await ProcessRunner.RunAsync(WslExe, ["-d", name, "-e", "/bin/sh", "-c", "echo $HOME"], timeout: TimeSpan.FromSeconds(10)))
                    .Replace("\0", "").Trim();
                if (!home.StartsWith('/')) continue;
                result.Add(new WslDistro(name, home, ToUnc(name, $"{home}/.claude/projects"), ToUnc(name, $"{home}/.copilot/session-state")));
            }
            catch (Exception) { /* distro not runnable */ }
        }
        // Only cache a non-empty result: WSL may simply not be ready yet.
        if (result.Count > 0) _cache = result;
        return result;
    }
}
