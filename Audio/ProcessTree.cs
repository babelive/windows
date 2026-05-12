using System.Runtime.InteropServices;

namespace Babelive.Audio;

/// <summary>
/// Walks the live process table via the Toolhelp32 snapshot API. Used by the
/// Teams-auto-route feature: Teams (especially "new Teams") emits audio from
/// <c>msedgewebview2.exe</c> children of <c>ms-teams.exe</c>, and per-app
/// audio routing in Windows is keyed on PID — so we have to route the whole
/// tree, not just the user-picked parent PID.
///
/// Pure P/Invoke (kernel32) — no extra NuGet dependency. Read-only.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// Return every PID whose ancestor chain includes a process matching one
    /// of <paramref name="rootProcessNames"/> (case-insensitive, without
    /// <c>.exe</c>). The roots themselves are included in the result.
    /// </summary>
    public static List<uint> EnumerateTree(params string[] rootProcessNames)
    {
        var result = new HashSet<uint>();
        var nameSet = new HashSet<string>(
            rootProcessNames.Select(n => n.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        // Snapshot every running process once. We need name + parent PID
        // for each entry, so a single TH32CS_SNAPPROCESS pass is enough.
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == InvalidHandleValue) return new List<uint>();
        try
        {
            // Stash all processes by parent PID so we can BFS downward.
            var childrenByParent = new Dictionary<uint, List<uint>>();
            var roots = new List<uint>();

            var pe = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>()
            };
            if (!Process32First(snap, ref pe)) return new List<uint>();
            do
            {
                uint pid = pe.th32ProcessID;
                uint parent = pe.th32ParentProcessID;

                if (!childrenByParent.TryGetValue(parent, out var list))
                {
                    list = new List<uint>();
                    childrenByParent[parent] = list;
                }
                list.Add(pid);

                // PROCESSENTRY32.szExeFile includes ".exe" — strip it.
                string exe = pe.szExeFile ?? string.Empty;
                int dot = exe.LastIndexOf('.');
                string baseName = dot > 0 ? exe[..dot] : exe;
                if (nameSet.Contains(baseName))
                    roots.Add(pid);
            } while (Process32Next(snap, ref pe));

            // BFS down from each matching root.
            var queue = new Queue<uint>(roots);
            while (queue.Count > 0)
            {
                uint cur = queue.Dequeue();
                if (!result.Add(cur)) continue;
                if (childrenByParent.TryGetValue(cur, out var kids))
                    foreach (var k in kids) queue.Enqueue(k);
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return result.ToList();
    }

    // ---- P/Invoke ---------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
