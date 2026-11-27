using System.Runtime.InteropServices;

namespace Tmax.Core.Pty;

/// <summary>A configured shell. Same shape as the original config's <c>shells</c> entries.</summary>
public sealed record ShellProfile(string Id, string Name, string Path, IReadOnlyList<string> Args)
{
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Cwd { get; init; }
}

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
            if (pwsh is not null) profiles.Add(new("pwsh", "PowerShell 7", pwsh, ["-NoLogo"]));

            var winPs = System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(winPs)) profiles.Add(new("powershell", "Windows PowerShell", winPs, []));

            var cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (File.Exists(cmd)) profiles.Add(new("cmd", "CMD", cmd, []));

            var wsl = System.IO.Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (File.Exists(wsl)) profiles.Add(new("wsl", "WSL", wsl, []));

            return profiles;
        }

        // Unix: the user's login shell first, then whatever /etc/shells lists.
        var login = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(login) && File.Exists(login))
            profiles.Add(Unix(login));

        var candidates = File.Exists("/etc/shells")
            ? File.ReadAllLines("/etc/shells").Select(l => l.Trim()).Where(l => l.StartsWith('/'))
            : ["/bin/zsh", "/bin/bash", "/bin/sh"];

        foreach (var shell in candidates.Where(c => InteractiveShells.Contains(System.IO.Path.GetFileName(c))))
        {
            if (File.Exists(shell) && profiles.All(p => p.Path != shell && p.Id != System.IO.Path.GetFileName(shell)))
                profiles.Add(Unix(shell));
        }

        return profiles;
    }

    // Login shells on macOS (Terminal.app convention, picks up /etc/zprofile PATH); plain on Linux.
    private static ShellProfile Unix(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return new(name, name, path, OperatingSystem.IsMacOS() ? ["-l"] : []);
    }

    // /etc/shells also lists sh, csh, ksh, dash …; offer only the usual interactive ones by default.
    private static readonly HashSet<string> InteractiveShells = ["zsh", "bash", "fish", "nu", "pwsh", "elvish", "xonsh"];

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
