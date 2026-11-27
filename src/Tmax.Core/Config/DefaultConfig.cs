using System.Text.Json;
using System.Text.Json.Nodes;
using Tmax.Core.Pty;

namespace Tmax.Core.Config;

/// <summary>Port of <c>defaultConfig</c> from the original src/main/config-store.ts.</summary>
public static class DefaultConfig
{
    private const string Static = """
    {
      "keybindings": [
      { "action": "createTerminal", "key": "Ctrl+T" },
      { "action": "createTerminal", "key": "Ctrl+Shift+N" },
      { "action": "closeTerminal", "key": "Ctrl+Shift+W" },
      { "action": "nextWorkspace", "key": "Ctrl+Shift+]" },
      { "action": "prevWorkspace", "key": "Ctrl+Shift+[" },
      { "action": "goToWorkspace1", "key": "Ctrl+1" },
      { "action": "goToWorkspace2", "key": "Ctrl+2" },
      { "action": "goToWorkspace3", "key": "Ctrl+3" },
      { "action": "goToWorkspace4", "key": "Ctrl+4" },
      { "action": "goToWorkspace5", "key": "Ctrl+5" },
      { "action": "goToWorkspace6", "key": "Ctrl+6" },
      { "action": "goToWorkspace7", "key": "Ctrl+7" },
      { "action": "goToWorkspace8", "key": "Ctrl+8" },
      { "action": "goToWorkspace9", "key": "Ctrl+9" },
      { "action": "focusUp", "key": "Shift+ArrowUp" },
      { "action": "focusDown", "key": "Shift+ArrowDown" },
      { "action": "focusLeft", "key": "Shift+ArrowLeft" },
      { "action": "focusRight", "key": "Shift+ArrowRight" },
      { "action": "moveRight", "key": "Ctrl+Shift+ArrowRight" },
      { "action": "moveDown", "key": "Ctrl+Shift+ArrowDown" },
      { "action": "moveLeft", "key": "Ctrl+Shift+ArrowLeft" },
      { "action": "moveUp", "key": "Ctrl+Shift+ArrowUp" },
      { "action": "splitHorizontal", "key": "Ctrl+Alt+ArrowRight" },
      { "action": "splitHorizontalLeft", "key": "Ctrl+Alt+ArrowLeft" },
      { "action": "splitVertical", "key": "Ctrl+Alt+ArrowDown" },
      { "action": "splitVerticalUp", "key": "Ctrl+Alt+ArrowUp" },
      { "action": "toggleFocusMode", "key": "Ctrl+Shift+F" },
      { "action": "toggleFloat", "key": "Ctrl+Shift+U" },
      { "action": "resizeUp", "key": "Ctrl+Shift+Alt+ArrowUp" },
      { "action": "resizeDown", "key": "Ctrl+Shift+Alt+ArrowDown" },
      { "action": "resizeLeft", "key": "Ctrl+Shift+Alt+ArrowLeft" },
      { "action": "resizeRight", "key": "Ctrl+Shift+Alt+ArrowRight" },
      { "action": "copySelection", "key": "Ctrl+Insert" }
      ],
      "theme": {
      "background": "#1e1e2e",
      "foreground": "#cdd6f4",
      "cursor": "#f5e0dc",
      "selectionBackground": "#585b70",
      "black": "#45475a",
      "red": "#f38ba8",
      "green": "#a6e3a1",
      "yellow": "#f9e2af",
      "blue": "#89b4fa",
      "magenta": "#f5c2e7",
      "cyan": "#94e2d5",
      "white": "#bac2de",
      "brightBlack": "#585b70",
      "brightRed": "#f38ba8",
      "brightGreen": "#a6e3a1",
      "brightYellow": "#f9e2af",
      "brightBlue": "#89b4fa",
      "brightMagenta": "#f5c2e7",
      "brightCyan": "#94e2d5",
      "brightWhite": "#a6adc8"
      },
      "terminal": {
        "fontSize": 14,
        "fontFamily": "CaskaydiaCove Nerd Font, CaskaydiaCove NF, Cascadia Code, Consolas, Menlo, monospace",
        "scrollback": 50000,
        "smartUnwrapCopy": true
      },
      "copilotCommand": "copilot",
      "claudeCodeCommand": "claude",
      "backgroundMaterial": "none",
      "backgroundOpacity": 0.8,
      "tabColorIntensity": 40,
      "aiSessionNotifications": true,
      "notificationExcludeStrings": [],
      "aiShimmerEnabled": true,
      "aiSessionLoadLimit": 314
    }
    """;

    public static JsonObject Create(IReadOnlyList<ShellProfile>? shells = null)
    {
        shells ??= ShellProfiles.Detect();
        var config = JsonNode.Parse(Static)!.AsObject();
        config["shells"] = JsonSerializer.SerializeToNode(shells, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        config["defaultShellId"] = shells.FirstOrDefault()?.Id ?? "";
        return config;
    }
}
