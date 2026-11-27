using Tmax.Core.Platform;

namespace Tmax.Core.Git;

internal static class Git
{
    public static Task<string> RunAsync(string cwd, params string[] args) =>
        ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromSeconds(60));

    /// <summary>git prints forward slashes; use the native separator like the original.</summary>
    public static string NativePath(string p) => p.Trim().Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Refs coming from the UI must not be parsed as options (e.g. "--upload-pack=…").</summary>
    public static void AssertRef(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(char.IsControl))
            throw new ArgumentException($"Invalid {what}: {value}");
    }
}
