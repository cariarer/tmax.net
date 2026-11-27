using System.Diagnostics;

namespace Tmax.App.Platform;

/// <summary>Opens files/folders with the OS default handler (Electron's shell.openPath).</summary>
public static class OsShell
{
    // Same blocklist as the original OPEN_PATH handler: extensions that execute code when "opened".
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".msi", ".com", ".scr", ".pif",
        ".lnk", ".hta", ".vbs", ".vbe", ".jse", ".wsf", ".wsh",
        ".reg", ".msc", ".cpl", ".chm",
        ".sh", ".app", ".command",
        ".jar", ".py", ".pyw",
    };

    public static bool IsDangerous(string path) => DangerousExtensions.Contains(Path.GetExtension(path));

    /// <returns>false when the path was refused or the launcher failed.</returns>
    public static bool OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsDangerous(path)) return false;
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
                psi = new ProcessStartInfo(path) { UseShellExecute = true };
            else
            {
                psi = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(path); // no shell involved: no quoting/injection issues
            }
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
