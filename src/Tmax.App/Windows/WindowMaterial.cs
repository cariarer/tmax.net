using System.Runtime.InteropServices;

namespace Tmax.App.Windows;

/// <summary>
/// Windows 11 (22H2+) backdrop materials: mica, acrylic, tabbed (replaces Electron's setBackgroundMaterial).
/// The window gets the DWM system backdrop; the WebView must be transparent for it to show, which
/// WebView2 only honours at creation time — so enabling a material from "none" needs a restart.
/// </summary>
public static class WindowMaterial
{
    public static readonly string[] Valid = ["none", "auto", "mica", "acrylic", "tabbed"];

    public static bool Supported => OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Call before the first window is created.</summary>
    public static void PrepareWebView(string? material)
    {
        if (Supported && material is not null and not "none")
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0"); // fully transparent
    }

    public static void Apply(IntPtr hwnd, string material)
    {
        if (!Supported || hwnd == IntPtr.Zero) return;
        var type = material switch
        {
            "auto" => 0, // DWMSBT_AUTO
            "mica" => 2, // DWMSBT_MAINWINDOW
            "acrylic" => 3, // DWMSBT_TRANSIENTWINDOW
            "tabbed" => 4, // DWMSBT_TABBEDWINDOW
            _ => 1, // DWMSBT_NONE
        };
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref type, sizeof(int));
        // Let the backdrop extend under the (transparent) client area.
        var margins = type == 1 ? new Margins() : new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
