using System.Diagnostics;

namespace Tmax.Core.Platform;

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

    /// <summary>Shows the item selected in Explorer / Finder; on Linux opens the containing folder.</summary>
    public static bool RevealInFolder(string path)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add($"/select,{Path.GetFullPath(path)}");
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
            }
            else
            {
                psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!);
            }
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Moves a file or folder to the Recycle Bin / Trash. Throws when that is not possible.</summary>
    public static async Task MoveToTrashAsync(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Run(() =>
            {
                const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
                const Microsoft.VisualBasic.FileIO.RecycleOption bin = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
                if (Directory.Exists(path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, bin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, bin);
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            // /usr/bin/trash ships with macOS 15+; Finder via AppleScript before that.
            if (File.Exists("/usr/bin/trash"))
                await ProcessRunner.RunAsync("/usr/bin/trash", [Path.GetFullPath(path)]);
            else
                await ProcessRunner.RunAsync("osascript",
                    ["-e", "on run argv", "-e", "tell application \"Finder\" to delete POSIX file (item 1 of argv)", "-e", "end run", path]);
        }
        else
        {
            await ProcessRunner.RunAsync("gio", ["trash", "--", path]);
        }
    }

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
