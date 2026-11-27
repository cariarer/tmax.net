using System.Collections.Concurrent;
using System.Drawing;
using Photino.NET;
using Tmax.App.Rpc;

namespace Tmax.App.Windows;

/// <summary>
/// The Photino windows: the main window plus detached terminal / backlog windows (replaces the
/// BrowserWindow handling in the original main.ts). Every window loads the same UI and talks to
/// the same backend; the query string selects the renderer mode (see renderer/window-mode.ts).
/// </summary>
public sealed class WindowManager(RpcServer rpc, ILogger<WindowManager> logger)
{
    public const string MainKey = "main";
    public const string BacklogKey = "backlog";
    private const string DetachedPrefix = "terminal:";

    private readonly ConcurrentDictionary<string, PhotinoWindow> _children = new();
    private PhotinoWindow? _main;
    private string _baseUrl = "";
    private string _fragment = "";
    private bool _devTools;

    public bool HasWindows => _main is not null;

    /// <summary>Creates the main window and blocks until it is closed.</summary>
    public void RunMain(string baseUrl, string fragment, bool devTools, string? iconFile)
    {
        _baseUrl = baseUrl;
        _fragment = fragment;
        _devTools = devTools;
        _main = Configure(new PhotinoWindow(), "tmax", 1280, 800, iconFile)
            .Load($"{baseUrl}/#{fragment}&win={MainKey}");
        _main.WaitForClose();
        _main = null;
    }

    public void CloseMain() => OnUi(() => _main?.Close());

    public void OpenDetachedTerminal(string terminalId)
    {
        if (!Ids.IsValidPaneId(terminalId)) throw new ArgumentException("Invalid terminal id");
        Open(DetachedPrefix + terminalId, $"?detachedTerminalId={Uri.EscapeDataString(terminalId)}", "tmax - Terminal", 800, 600,
            onClosed: () => rpc.Broadcast("detach:closed", terminalId));
    }

    public void OpenBacklog() =>
        Open(BacklogKey, "?detachedBacklog=1", "tmax - Backlog", 1200, 800,
            onClosed: () => rpc.Broadcast("backlogWindow:closed"));

    public void Close(string key) => OnUi(() =>
    {
        if (_children.TryGetValue(key, out var w)) w.Close();
    });

    public void Focus(string key) => OnUi(() => FocusNow(key));

    private void FocusNow(string key)
    {
        var w = key == MainKey ? _main : _children.GetValueOrDefault(key);
        if (w is null) return;
        if (w.Minimized) w.SetMinimized(false);
        // Photino has no focus API; toggling topmost brings the window to the front on all platforms.
        w.SetTopMost(true);
        w.SetTopMost(false);
    }

    public static string DetachedKey(string terminalId) => DetachedPrefix + terminalId;

    /// <summary>Title of the window a renderer runs in (renderers mirror document.title here).</summary>
    public void SetTitle(string key, string title)
    {
        title = title.Length > 200 ? title[..200] : title;
        OnUi(() =>
        {
            var w = key == MainKey ? _main : _children.GetValueOrDefault(key);
            if (w is not null && w.Title != title) w.SetTitle(title);
        });
    }

    private void Open(string key, string query, string title, int width, int height, Action onClosed)
    {
        if (_main is null) throw new InvalidOperationException("Separate windows are only available in the desktop app (not with --headless).");
        OnUi(() =>
        {
            if (_children.ContainsKey(key))
            {
                FocusNow(key);
                return;
            }
            var window = Configure(new PhotinoWindow(_main), title, width, height, _main.IconFile);
            window.WindowClosing += (_, _) =>
            {
                _children.TryRemove(key, out _);
                onClosed();
                return false; // allow closing
            };
            _children[key] = window;
            window.Load($"{_baseUrl}/{query}#{_fragment}&win={Uri.EscapeDataString(key)}");
            window.WaitForClose(); // for child windows this shows the window and returns
        });
    }

    private PhotinoWindow Configure(PhotinoWindow w, string title, int width, int height, string? iconFile)
    {
        w.SetTitle(title)
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(width, height))
            .Center()
            .SetDevToolsEnabled(_devTools)
            .SetContextMenuEnabled(_devTools)
            // navigator.clipboard (paste/copy in the terminal) needs this in WebKitGTK/WebView2.
            .SetJavascriptClipboardAccessEnabled(true);
        if (!string.IsNullOrEmpty(iconFile) && File.Exists(iconFile)) w.SetIconFile(iconFile);
        return w;
    }

    private void OnUi(Action action)
    {
        var main = _main;
        if (main is null) return;
        try
        {
            main.Invoke(action);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Window operation failed");
        }
    }
}
