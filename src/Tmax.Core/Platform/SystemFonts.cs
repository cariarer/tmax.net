using System.Text.RegularExpressions;

namespace Tmax.Core.Platform;

/// <summary>Installed font families for the Settings font picker. Like the original, Windows only (empty elsewhere).</summary>
public static partial class SystemFonts
{
    public static List<string> List()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) continue;
            foreach (var value in key.GetValueNames())
            {
                // "Cascadia Code Bold (TrueType)" -> "Cascadia Code"; "A & B (TrueType)" -> both.
                var name = Suffix().Replace(value, "");
                foreach (var part in name.Split(" & "))
                    names.Add(Style().Replace(part, "").Trim());
            }
        }
        names.Remove("");
        return [.. names];
    }

    [GeneratedRegex(@"\s*\((TrueType|OpenType|All res|VGA res)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();

    [GeneratedRegex(@"\s+(Bold|Italic|Light|Semibold|SemiBold|Semilight|Black|Regular|Oblique|Medium|Thin|ExtraLight|ExtraBold|Condensed)(\s+(Bold|Italic|Light|Semibold|Black|Regular|Oblique|Medium|Thin|Condensed))*$", RegexOptions.IgnoreCase)]
    private static partial Regex Style();
}
