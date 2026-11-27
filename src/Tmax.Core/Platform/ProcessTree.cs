using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tmax.Core.Platform;

/// <summary>
/// Names of a process's descendants (port of src/main/process-tree.ts), used to detect AI CLIs
/// running inside a pane. Windows reads a Toolhelp32 snapshot (one syscall, no wmic process —
/// wmic is deprecated and was the source of the original's CPU spikes); Unix uses pgrep -P.
/// </summary>
public static partial class ProcessTree
{
    private const int MaxDepth = 3;

    /// <summary>Lowercase names without path or .exe, up to three levels deep. Empty on error.</summary>
    public static async Task<List<string>> GetDescendantNamesAsync(int rootPid)
    {
        if (rootPid <= 0) return [];
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var snapshot = WindowsSnapshot();
                return await Walk(rootPid, pid => Task.FromResult(snapshot.Where(p => p.Parent == pid).Select(p => (p.Pid, p.Name)).ToList()));
            }
            return await Walk(rootPid, UnixChildrenAsync);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<List<string>> Walk(int rootPid, Func<int, Task<List<(int Pid, string Name)>>> children)
    {
        var names = new List<string>();
        var visited = new HashSet<int>();

        async Task Visit(int pid, int depth)
        {
            if (depth >= MaxDepth || !visited.Add(pid)) return;
            foreach (var (childPid, name) in await children(pid))
            {
                if (childPid == pid) continue;
                names.Add(CleanName(name));
                await Visit(childPid, depth + 1);
            }
        }

        await Visit(rootPid, 0);
        return names;
    }

    public static string CleanName(string name)
    {
        var n = Path.GetFileName(name.Trim());
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.ToLowerInvariant();
    }

    private static async Task<List<(int, string)>> UnixChildrenAsync(int pid)
    {
        string output;
        try
        {
            output = await ProcessRunner.RunAsync("pgrep", ["-P", pid.ToString(), "-l"], timeout: TimeSpan.FromMilliseconds(2500));
        }
        catch (ProcessFailedException)
        {
            return []; // exit code 1 = no children
        }
        var list = new List<(int, string)>();
        foreach (var line in output.Split('\n'))
        {
            var m = PgrepLine().Match(line.Trim());
            if (m.Success && int.TryParse(m.Groups[1].Value, out var child)) list.Add((child, m.Groups[2].Value));
        }
        return list;
    }

    [GeneratedRegex(@"^(\d+)\s+(.+)$")]
    private static partial Regex PgrepLine();

    // ── Windows: Toolhelp32 ──────────────────────────────────────────

    private record struct WinProc(int Pid, int Parent, string Name);

    private static List<WinProc> WindowsSnapshot()
    {
        var result = new List<WinProc>();
        var snap = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return result;
            do
            {
                result.Add(new WinProc((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            } while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
