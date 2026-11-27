using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tmax.App.Platform;
using Tmax.Core.Config;

namespace Tmax.App.Rpc;

/// <summary>Config, session, keybindings, clipboard images and other small main-process handlers.</summary>
public static class AppChannels
{
    public static void Register(RpcServer rpc, ConfigStore config, SessionStore session, ILogger logger)
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

        // ── Keybindings (stored in the config; no separate keybindings file yet) ──
        rpc.Handle("keybindings:get", (_, _) => Task.FromResult<object?>(config.Get("keybindings") ?? new JsonArray()));
        rpc.Handle("keybindings:openFile", (_, _) =>
        {
            OsShell.OpenPath(config.Path);
            return Task.CompletedTask;
        });
        rpc.Handle("keybindings:reset", (_, _) =>
        {
            var defaults = config.Defaults["keybindings"]?.DeepClone();
            config.Set("keybindings", defaults);
            rpc.Broadcast("keybindings:changed", defaults);
            return Task.FromResult<object?>(defaults);
        });

        // ── Session ──────────────────────────────────────────────────
        var noRestore = Environment.GetCommandLineArgs().Contains("--no-restore");
        rpc.Handle("session:save", (args, _) =>
        {
            if (!noRestore) session.Save(ToNode(args, 0));
            return Task.CompletedTask;
        });
        rpc.Handle("session:load", (_, _) => Task.FromResult<object?>(noRestore ? null : session.Load()));
        // Used by OS notifications for AI sessions (not ported yet); accepted and ignored.
        rpc.Handle("session:nameOverridesSync", (_, _) => Task.CompletedTask);

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
        var version = typeof(AppChannels).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        rpc.Handle("version:getAppVersion", (_, _) => Task.FromResult<object?>(version));
        rpc.Handle("diag:log", (args, _) =>
        {
            logger.LogDebug("diag {Event} {Data}", args.Length > 0 ? args[0].ToString() : "", args.Length > 1 ? args[1].ToString() : "");
            return Task.CompletedTask;
        });
    }

    private static JsonNode? ToNode(JsonElement[] args, int index) =>
        index < args.Length ? JsonNode.Parse(args[index].GetRawText()) : null;
}
