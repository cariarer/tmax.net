namespace Tmax.Core.Platform;

/// <summary>Port of src/main/utils/security-guards.ts.</summary>
public static class PathGuard
{
    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when <paramref name="path"/> is the root itself or below it (no sibling-prefix match).</summary>
    public static bool IsWithin(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(r, p, Cmp) || p.StartsWith(r + Path.DirectorySeparatorChar, Cmp);
    }

    /// <summary>Resolves <paramref name="relative"/> against <paramref name="root"/>; throws if it escapes.</summary>
    public static string ResolveWithin(string root, string relative)
    {
        var resolved = Path.GetFullPath(relative, Path.GetFullPath(root));
        if (!IsWithin(root, resolved)) throw new UnauthorizedAccessException("Path traversal detected");
        return resolved;
    }

    public static bool IsValidWslDistro(string? distro) =>
        !string.IsNullOrEmpty(distro) && (char.IsLetterOrDigit(distro[0]) || distro[0] == '_')
        && distro.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>A bare file name: no separators, not "." / "..".</summary>
    public static bool IsPlainName(string? name) =>
        !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\']) < 0 && name is not ("." or "..");
}
