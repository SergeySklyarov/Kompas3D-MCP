using System.Runtime.InteropServices;
using System.Text;

namespace KompasMcp.Api5Adapter.Com;

/// <summary>Which process holds a file open, through the Windows Restart Manager.</summary>
/// <remarks>WHY this API. <c>System.IO</c> reports that a file is locked but never by whom, and the plain
/// Win32 route (<c>NtQuerySystemInformation</c>) is undocumented. The Restart Manager is the documented
/// Microsoft mechanism for "which applications are using this file" and is available on every supported
/// Windows. It is BEST EFFORT: any failure yields null, and the refusal simply omits the holder.
/// DOC: Restart Manager API (RmStartSession / RmRegisterResources / RmGetList).
/// History: docs/decisions/files.md#file-locked-guard</remarks>
internal static class FileLockOwner
{
    private const int ErrorMoreData = 234;
    private const int ErrorSuccess = 0;

    /// <summary>A human-readable holder — "name (pid N)" — or null when the OS cannot name one.</summary>
    public static string? TryFind(string path)
    {
        var sessionKey = Guid.NewGuid().ToString("N");
        if (RmStartSession(out var session, 0, sessionKey) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            var resources = new[] { path };
            if (RmRegisterResources(session, 1, resources, 0, null, 0, null) != ErrorSuccess)
            {
                return null;
            }

            var needed = 0u;
            var count = 0u;
            var reasons = 0u;
            var status = RmGetList(session, out needed, ref count, null, ref reasons);
            if (status == ErrorMoreData && needed > 0)
            {
                var buffer = new RmProcessInfo[needed];
                count = needed;
                status = RmGetList(session, out needed, ref count, buffer, ref reasons);
                if (status == ErrorSuccess)
                {
                    return Describe(buffer, count);
                }
            }
            else if (status == ErrorSuccess)
            {
                return Describe(Array.Empty<RmProcessInfo>(), count);
            }

            return null;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string? Describe(RmProcessInfo[] entries, uint count)
    {
        var self = Environment.ProcessId;
        var holders = new List<string>();
        for (var i = 0; i < count && i < entries.Length; i++)
        {
            var pid = entries[i].Process.dwProcessId;
            if (pid == self)
            {
                // Our own probe handle is not "another process holding the file".
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entries[i].strAppName) ? "<неизвестно>" : entries[i].strAppName;
            holders.Add($"{name} (pid {pid})");
        }

        return holders.Count == 0 ? null : string.Join("; ", holders);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;

        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RmAppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public RmAppType ApplicationType;

        public uint AppStatus;

        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        RmUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}
