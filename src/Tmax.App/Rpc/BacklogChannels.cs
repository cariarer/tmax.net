using Tmax.App.Windows;
using Tmax.Core.Backlog;

namespace Tmax.App.Rpc;

/// <summary>Backlog board channels (original backlog-service.ts registerBacklogHandlers + main.ts extras).</summary>
public static class BacklogChannels
{
    public static void Register(RpcServer rpc, WindowManager windows)
    {
        rpc.Handle("backlog:listTasks", async (a, _) =>
            await BacklogService.ListAllAsync(a.Opt<List<BacklogProjectRef>>(0), a.Opt<bool?>(1) ?? false));
        rpc.Handle("backlog:getTask", (a, _) =>
            Task.FromResult<object?>(BacklogService.GetTask(a.Get<string>(0), a.Get<string>(1), a.Get<string>(2))));
        rpc.Handle("backlog:editTask", (a, _) => Task.FromResult<object?>(BacklogWriter.Edit(a.Get<EditPayload>(0))));
        rpc.Handle("backlog:createTask", (a, _) => Task.FromResult<object?>(BacklogWriter.Create(a.Get<CreatePayload>(0))));
        rpc.Handle("backlog:archiveTask", (a, _) => Task.FromResult<object?>(BacklogWriter.Archive(a.Get<string>(0), a.Get<string>(1))));
        rpc.Handle("backlog:deleteTask", async (a, _) => await BacklogService.DeleteAsync(a.Get<string>(0), a.Get<string>(1)));
        rpc.Handle("backlog:validateProject", (a, _) => Task.FromResult<object?>(new { ok = BacklogService.IsProject(a.Get<string>(0)) }));
        rpc.Handle("backlog:initProject", (a, _) => Task.FromResult<object?>(BacklogWriter.InitProject(a.Get<string>(0), a.Opt<string>(1))));
        rpc.Handle("backlog:pickFolder", async (a, _) => await windows.PickFolderAsync("Select a project folder", a.Opt<string>(0)));
        // tmax.net: the frontend sends the clipboard image (base64 PNG); main can't read the WebView clipboard.
        rpc.Handle("backlog:saveImage", async (a, _) =>
            await BacklogService.SaveImageAsync(a.Get<string>(0), Convert.FromBase64String(a.Get<string>(1))));
    }
}
