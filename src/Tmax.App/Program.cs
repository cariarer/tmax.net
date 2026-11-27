using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Tmax.App.Rpc;
using Tmax.App.Updates;
using Tmax.App.Windows;
using Velopack;
using Tmax.Core.Config;
using Tmax.Core.Pty;

namespace Tmax.App;

public static class Program
{
    [STAThread] // WebView2 on Windows requires an STA main thread.
    public static int Main(string[] argv)
    {
        // Velopack install/update/uninstall hooks run here and may exit the process; must be first.
        VelopackApp.Build().Run();

        var headless = argv.Contains("--headless");
        // Frontend dev server (Vite). When set, the window loads it instead of the bundled wwwroot.
        var devUrl = Environment.GetEnvironmentVariable("TMAX_DEV_URL")?.TrimEnd('/');
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var app = BuildBackend(token, devUrl);
        app.StartAsync().GetAwaiter().GetResult();
        var backendUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(backendUrl).Port;

        // The token travels in the fragment, which browsers never send over the network or into logs.
        var fragment = $"port={port}&token={token}";
        var uiUrl = $"{devUrl ?? backendUrl}/#{fragment}";
        var windows = app.Services.GetRequiredService<WindowManager>();

        // Ctrl+C / SIGTERM / host shutdown: close the window so WaitForClose returns and we exit cleanly.
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Shutdown(); });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; Shutdown(); });
        void Shutdown()
        {
            if (windows.HasWindows) windows.CloseMain();
            else app.Lifetime.StopApplication();
        }

        try
        {
            if (headless)
            {
                // Backend only: open the URL in a normal browser (handy for frontend work and tests).
                Console.WriteLine($"TMAX_UI_URL={uiUrl}");
                Console.Out.Flush();
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
            }
            else
            {
                // Dev/test aid only: lets scripts connect to a desktop instance. Off unless explicitly set.
                if (Environment.GetEnvironmentVariable("TMAX_PRINT_URL") == "1") Console.WriteLine($"TMAX_UI_URL={uiUrl}");
                windows.RunMain(devUrl ?? backendUrl, fragment, devTools: devUrl is not null,
                    iconFile: Path.Combine(AppContext.BaseDirectory, "wwwroot", "icon.png"),
                    material: app.Services.GetRequiredService<ConfigStore>().Get<string>("backgroundMaterial") ?? "none");
            }
        }
        finally
        {
            app.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return 0;
    }

    private static WebApplication BuildBackend(string token, string? devUrl)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        // Loopback only, OS-assigned port: nothing outside this machine can reach the terminals.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<RpcServer>();
        builder.Services.AddSingleton<WindowManager>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton(_ => new ConfigStore());
        builder.Services.AddSingleton(_ => new SessionStore());
        builder.Services.AddSingleton(_ => new KeybindingsFile(AppPaths.DataDir));
        builder.Services.AddSingleton(_ => new DiagLog());
        // Spawning is limited to the shells in the config, read fresh on every spawn.
        builder.Services.AddSingleton(sp =>
        {
            var rpc = sp.GetRequiredService<RpcServer>();
            var titles = new PaneTitleFiles(
                PaneTitleFiles.PrepareProcessDir(Path.Combine(AppPaths.DataDir, "pane-titles")),
                (id, title) => rpc.Broadcast(PtyChannels.ExternalTitle, id, title));
            return new PtyManager(() => sp.GetRequiredService<ConfigStore>().Shells, titles);
        });

        var app = builder.Build();
        var rpc = app.Services.GetRequiredService<RpcServer>();
        var pty = app.Services.GetRequiredService<PtyManager>();
        var config = app.Services.GetRequiredService<ConfigStore>();
        PtyChannels.Register(rpc, pty);
        PlatformChannels.Register(rpc, pty);
        var diag = app.Services.GetRequiredService<DiagLog>();
        diag.Log("app:start", new { version = typeof(Program).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString() });
        AppChannels.Register(rpc, config, app.Services.GetRequiredService<SessionStore>(),
            app.Services.GetRequiredService<KeybindingsFile>(), diag,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("tmax"));
        GitFileChannels.Register(rpc);
        WindowChannels.Register(rpc, app.Services.GetRequiredService<WindowManager>(), config);
        BacklogChannels.Register(rpc, app.Services.GetRequiredService<WindowManager>());
        AiSessionChannels.Register(rpc, config, app.Services.GetRequiredService<SessionStore>(), pty,
            app.Services.GetRequiredService<WindowManager>(), app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ai-sessions"));
        var updates = app.Services.GetRequiredService<UpdateService>();
        VersionChannels.Register(rpc, updates);
        updates.Start();

        app.UseWebSockets();
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            if (!IsAuthorized(ctx, token, devUrl)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // End connections when the app stops; otherwise an open window/client blocks shutdown forever.
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted,
                ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            await rpc.RunConnectionAsync(socket, lifetime.Token);
            return Results.Empty;
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        return app;
    }

    /// <summary>
    /// Any local process or web page could try to open a WebSocket to us, and an open
    /// terminal is arbitrary code execution. Require the per-launch token and, for
    /// browser clients, an Origin that is our own UI.
    /// </summary>
    private static bool IsAuthorized(HttpContext ctx, string token, string? devUrl)
    {
        var supplied = ctx.Request.Query["token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(token)))
            return false;

        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // non-browser client that knows the token

        var self = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        return origin == self || (devUrl is not null && origin == devUrl);
    }
}

public static class PlatformChannels
{
    public static void Register(RpcServer rpc, PtyManager pty)
    {
        // Replaces the synchronous window.platformInfo from the Electron preload;
        // the frontend fetches it once before rendering.
        rpc.Handle("platform:getInfo", (_, _) => Task.FromResult<object?>(new
        {
            platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
            homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            isDev = Environment.GetEnvironmentVariable("TMAX_DEV_URL") is not null,
            windowsBuildNumber = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0,
            arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        }));
        rpc.Handle("shell:getProfiles", (_, _) => Task.FromResult<object?>(pty.Profiles));
    }
}
