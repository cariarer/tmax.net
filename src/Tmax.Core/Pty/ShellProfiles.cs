using System.Runtime.InteropServices;

namespace Tmax.Core.Pty;

public sealed record ShellProfile(string Name, string Path, IReadOnlyList<string> Args);

/// <summary>
/// Detects the shells available on this machine. Spawning is restricted to these
/// profiles so a compromised frontend cannot launch arbitrary executables
/// (same rule as the original app's PTY_CREATE handler).
/// </summary>
public static class ShellProfiles
{
    public static IReadOnlyList<ShellProfile> Detect()
    {
        var profiles = new List<ShellProfile>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pwsh = FindOnPath("pwsh.exe");
            if (pwsh is not null) profiles.Add(new("PowerShell", pwsh, ["-NoLogo"]));

            var winPs = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(winPs)) profiles.Add(new("Windows PowerShell", winPs, ["-NoLogo"]));

            var cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (File.Exists(cmd)) profiles.Add(new("Command Prompt", cmd, []));

            var wsl = System.IO.Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (File.Exists(wsl)) profiles.Add(new("WSL", wsl, []));

            return profiles;
        }

        // Unix: the user's login shell first, then whatever /etc/shells lists.
        var login = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(login) && File.Exists(login))
            profiles.Add(new(System.IO.Path.GetFileName(login), login, ["-l"]));

        var candidates = File.Exists("/etc/shells")
            ? File.ReadAllLines("/etc/shells").Select(l => l.Trim()).Where(l => l.StartsWith('/'))
            : ["/bin/zsh", "/bin/bash", "/bin/sh"];

        foreach (var shell in candidates)
        {
            if (File.Exists(shell) && profiles.All(p => p.Path != shell))
                profiles.Add(new(System.IO.Path.GetFileName(shell), shell, ["-l"]));
        }

        return profiles;
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir.Trim('"'), exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
