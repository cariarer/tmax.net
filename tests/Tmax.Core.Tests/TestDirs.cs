namespace Tmax.Core.Tests;

internal static class TestDirs
{
    /// <summary>
    /// Deletes a temp test directory. Git marks its object files read-only, which makes
    /// Directory.Delete fail on Windows, so clear the attribute first. Best effort.
    /// </summary>
    public static void Delete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A process may still hold a file (e.g. git on Windows); the OS cleans temp up eventually.
        }
    }
}
