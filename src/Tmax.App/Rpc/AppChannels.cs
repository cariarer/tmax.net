using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tmax.Core.Platform;
using Tmax.Core.Config;

namespace Tmax.App.Rpc;

/// <summary>Config, session, keybindings, clipboard images and other small main-process handlers.</summary>
public static class AppChannels
{
    public static void Register(RpcServer rpc, ConfigStore config, SessionStore session, KeybindingsFile keybindings, DiagLog diag, ILogger logger)
    {
        // ── Config ───────────────────────────────────────────────────
        rpc.Handle("config:get", (_, _) => Task.FromResult<object?>(config.GetAll()));
        rpc.Handle("config:set", (args, _) =>
        {
            config.Set(args.Get<string>(0), ToNode(args, 1));
            return Task.CompletedTask;
        });
        rpc.Handle("config:open", (_, _) =>
        {
            OsShell.OpenPath(config.Path);
            return Task.CompletedTask;
        });

        // ── Keybindings (keybindings.json is authoritative; seeded from the config once) ──
        var seed = config.Get<List<Keybinding>>("keybindings") ?? [];
        config.Set("keybindings", JsonSerializer.SerializeToNode(keybindings.Init(seed, w => logger.LogWarning("{Warning}", w)), RpcServer.Json));
        keybindings.Changed += bindings =>
        {
            var node = JsonSerializer.SerializeToNode(bindings, RpcServer.Json);
            config.Set("keybindings", node);
            rpc.Broadcast("keybindings:changed", node);
        };
        keybindings.StartWatching();
        rpc.Handle("keybindings:get", (_, _) => Task.FromResult<object?>(keybindings.Read()));
        rpc.Handle("keybindings:openFile", (_, _) =>
        {
            OsShell.OpenPath(keybindings.Path);
            return Task.CompletedTask;
        });
        rpc.Handle("keybindings:reset", (_, _) =>
        {
            var defaults = config.Defaults["keybindings"].Deserialize<List<Keybinding>>(RpcServer.Json) ?? [];
            var bindings = keybindings.ResetToDefaults(defaults);
            var node = JsonSerializer.SerializeToNode(bindings, RpcServer.Json);
            config.Set("keybindings", node);
            rpc.Broadcast("keybindings:changed", node);
            return Task.FromResult<object?>(bindings);
        });

        // ── Session ──────────────────────────────────────────────────
        var noRestore = Environment.GetCommandLineArgs().Contains("--no-restore");
        rpc.Handle("session:save", (args, _) =>
        {
            if (!noRestore) session.Save(ToNode(args, 0));
            return Task.CompletedTask;
        });
        rpc.Handle("session:load", (_, _) => Task.FromResult<object?>(noRestore ? null : session.Load()));

        // ── Shell ────────────────────────────────────────────────────
        rpc.Handle("shell:openPath", (args, _) =>
        {
            var path = args.Get<string>(0);
            if (OsShell.IsDangerous(path)) logger.LogWarning("Blocked openPath for {Extension}", Path.GetExtension(path));
            else OsShell.OpenPath(path);
            return Task.CompletedTask;
        });

        // ── Clipboard images ─────────────────────────────────────────
        rpc.Handle("clipboard:saveImage", async (args, ct) =>
        {
            var bytes = Convert.FromBase64String(args.Get<string>(0));
            var dir = AppPaths.ClipboardDir;
            Directory.CreateDirectory(dir);
            var name = $"clipboard-{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}.png";
            var file = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(file, bytes, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return file;
        });
        rpc.Handle("image:resolveClipboardBasename", (args, _) =>
        {
            var name = args.Get<string>(0);
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
                return Task.FromResult<object?>(null);
            var full = Path.Combine(AppPaths.ClipboardDir, name);
            return Task.FromResult<object?>(File.Exists(full) ? full : null);
        });
        rpc.Handle("image:readDataUrl", async (args, ct) =>
        {
            var file = args.Get<string>(0);
            var mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => null,
            };
            if (mime is null || !File.Exists(file) || new FileInfo(file).Length > 50 * 1024 * 1024) return null;
            return $"data:{mime};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(file, ct))}";
        });

        // ── Misc ─────────────────────────────────────────────────────
        rpc.Handle("diag:log", (args, _) =>
        {
            if (args.Length > 0 && args[0].ValueKind == JsonValueKind.String)
                diag.Log(args[0].GetString()!, args.Length > 1 ? ToNode(args, 1) : null);
            return Task.CompletedTask;
        });
        rpc.Handle("diag:getLogPath", (_, _) => Task.FromResult<object?>(diag.Path));
        rpc.Handle("diag:readTail", (args, _) => Task.FromResult<object?>(diag.ReadTail(args.Opt<int?>(0) ?? 25 * 1024)));
        rpc.Handle("system:getFonts", (_, _) => Task.Run<object?>(SystemFonts.List));
    }

    private static JsonNode? ToNode(JsonElement[] args, int index) =>
        index < args.Length ? JsonNode.Parse(args[index].GetRawText()) : null;
}
