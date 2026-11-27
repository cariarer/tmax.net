using System.Text.Json.Nodes;
using Tmax.App.Windows;
using Tmax.Core.Config;

namespace Tmax.App.Rpc;

public static class WindowChannels
{
    public static void Register(RpcServer rpc, WindowManager windows, ConfigStore config)
    {
        rpc.Handle("transparency:platformSupports", (_, _) => Task.FromResult<object?>(WindowMaterial.Supported));
        rpc.Handle("transparency:setMaterial", (a, _) =>
        {
            var material = a.Get<string>(0);
            if (!WindowMaterial.Supported || !WindowMaterial.Valid.Contains(material)) return Task.CompletedTask;
            config.Set("backgroundMaterial", JsonValue.Create(material));
            windows.SetMaterial(material);
            return Task.CompletedTask;
        });
        rpc.Handle("detach:create", (a, _) => { windows.OpenDetachedTerminal(a.Get<string>(0)); return Task.CompletedTask; });
        rpc.Handle("detach:close", (a, _) => { windows.Close(WindowManager.DetachedKey(a.Get<string>(0))); return Task.CompletedTask; });
        rpc.Handle("detach:focus", (a, _) => { windows.Focus(WindowManager.DetachedKey(a.Get<string>(0))); return Task.CompletedTask; });
        rpc.Handle("backlogWindow:open", (_, _) => { windows.OpenBacklog(); return Task.CompletedTask; });
        rpc.Handle("backlogWindow:close", (_, _) => { windows.Close(WindowManager.BacklogKey); return Task.CompletedTask; });
        // tmax.net: renderers mirror document.title so the OS window title follows the focused pane.
        rpc.Handle("window:setTitle", (a, _) => { windows.SetTitle(a.Get<string>(0), a.Get<string>(1)); return Task.CompletedTask; });
    }
}
