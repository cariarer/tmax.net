namespace Tmax.Core.Config;

/// <summary>Where tmax.net keeps its files. Separate from the Electron tmax so both can coexist.</summary>
public static class AppPaths
{
    public const string AppName = "tmax.net";

    /// <summary>
    /// %APPDATA%\tmax.net on Windows, ~/Library/Application Support/tmax.net on macOS,
    /// $XDG_CONFIG_HOME/tmax.net (default ~/.config/tmax.net) on Linux.
    /// Overridable with TMAX_DATA_DIR (tests, portable installs).
    /// </summary>
    public static string DataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("TMAX_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", AppName);
            // ApplicationData = %APPDATA% on Windows and $XDG_CONFIG_HOME or ~/.config on Linux.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        }
    }

    /// <summary>Stable temp dir for pasted clipboard images (paths stay clickable across restarts).</summary>
    public static string ClipboardDir => Path.Combine(Path.GetTempPath(), "tmax-clipboard");
}
