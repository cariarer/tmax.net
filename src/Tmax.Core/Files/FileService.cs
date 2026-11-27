using Tmax.Core.Platform;

namespace Tmax.Core.Files;

public sealed record FileEntry(string Name, bool IsDirectory, string Path);

public sealed record FileResult(bool Ok, string? Error = null, string? NewPath = null);

/// <summary>
/// File explorer operations (port of the FILE_* handlers in main.ts). Paths are "logical":
/// Windows/Unix paths, or Linux paths inside a WSL distro, translated to //wsl.localhost/….
/// </summary>
public static class FileService
{
    public const long MaxReadBytes = 1024 * 1024;

    /// <returns>null when the WSL distro name is invalid.</returns>
    public static string? ToFsPath(string logicalPath, string? wslDistro)
    {
        if (string.IsNullOrEmpty(wslDistro) || !logicalPath.StartsWith('/')) return logicalPath;
        if (!PathGuard.IsValidWslDistro(wslDistro)) return null;
        return $"//wsl.localhost/{wslDistro}{logicalPath}";
    }

    public static List<FileEntry> List(string dirPath, string? wslDistro)
    {
        var fsPath = ToFsPath(dirPath, wslDistro);
        if (fsPath is null) return [];
        try
        {
            var isWsl = !string.IsNullOrEmpty(wslDistro) && dirPath.StartsWith('/');
            return new DirectoryInfo(fsPath).EnumerateFileSystemInfos()
                .Select(e => new FileEntry(e.Name, e is DirectoryInfo,
                    isWsl ? dirPath.TrimEnd('/') + "/" + e.Name : Path.Combine(dirPath, e.Name)))
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>Text files under 1 MB only; null for binaries, big or unreadable files.</summary>
    public static async Task<string?> ReadAsync(string filePath, string? wslDistro)
    {
        var fsPath = ToFsPath(filePath, wslDistro);
        if (fsPath is null) return null;
        try
        {
            if (new FileInfo(fsPath).Length > MaxReadBytes) return null;
            var content = await File.ReadAllTextAsync(fsPath);
            return content.Contains('\0') ? null : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static FileResult Reveal(string filePath, string? wslDistro)
    {
        var fsPath = ToFsPath(filePath, wslDistro);
        if (fsPath is null) return new(false, "Invalid path");
        if (!File.Exists(fsPath) && !Directory.Exists(fsPath)) return new(false, $"Path not found: {fsPath}");
        return OsShell.RevealInFolder(fsPath) ? new(true) : new(false, "Could not open the file manager");
    }

    public static FileResult Rename(string oldPath, string newName, string? wslDistro)
    {
        if (!PathGuard.IsPlainName(newName)) return new(false, "Invalid name");
        var fsOld = ToFsPath(oldPath, wslDistro);
        if (fsOld is null) return new(false, "Invalid path");
        try
        {
            var fsNew = Path.Combine(Path.GetDirectoryName(fsOld)!, newName);
            if (File.Exists(fsNew) || Directory.Exists(fsNew)) return new(false, "A file with that name already exists");
            if (Directory.Exists(fsOld)) Directory.Move(fsOld, fsNew);
            else File.Move(fsOld, fsNew);

            var newLogical = !string.IsNullOrEmpty(wslDistro) && oldPath.StartsWith('/')
                ? oldPath[..(oldPath.LastIndexOf('/') + 1)] + newName
                : Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
            return new(true, NewPath: newLogical);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>Moves to the Recycle Bin / Trash (recoverable), never deletes permanently.</summary>
    public static async Task<FileResult> TrashAsync(string filePath, string? wslDistro)
    {
        var fsPath = ToFsPath(filePath, wslDistro);
        if (fsPath is null) return new(false, "Invalid path");
        if (!File.Exists(fsPath) && !Directory.Exists(fsPath)) return new(false, $"Path not found: {fsPath}");
        try
        {
            await OsShell.MoveToTrashAsync(fsPath);
            return new(true);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }
}
