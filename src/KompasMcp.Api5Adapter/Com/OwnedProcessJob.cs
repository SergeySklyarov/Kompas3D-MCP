using System.Runtime.InteropServices;
using KompasMcp.Domain.Applications;

namespace KompasMcp.Api5Adapter.Com;

/// <summary>Holds a launched instance inside a job object, so it cannot outlive this process.</summary>
/// <remarks>DOC: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects - closing the
/// last handle of a job with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE terminates its processes; "Creating
/// Jobs" documents AssignProcessToJobObject for an ALREADY RUNNING process, which a COM launch is.
/// MEASURED: the launched instance's parent is <c>svchost.exe</c>, so a tree kill never reaches it; in
/// this job it ends ~0.3 s after the last handle closes.
/// LIMIT: the handle is never closed while the process lives and is not a SafeHandle (a finalizer would
/// end a healthy instance); the OS closes it at exit, which is when the job fires.
/// History: docs/decisions/host.md#client-shutdown</remarks>
public sealed class OwnedProcessJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;

    private readonly IntPtr _handle;

    private OwnedProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>Puts one process into a kill-on-close job, or names the Win32 step that refused.</summary>
    /// <remarks>The refusal is returned, not thrown: the session keeps working as it did before, and the
    /// caller publishes the text. <paramref name="failure"/> is null exactly when the guard is in place.</remarks>
    public static OwnedProcessJob? TryCreate(int processId, out string? failure)
    {
        failure = null;
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            failure = OwnedInstanceGuard.FailureText("CreateJobObject", Marshal.GetLastWin32Error());
            return null;
        }

        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            failure = OwnedInstanceGuard.FailureText("SetInformationJobObject", Marshal.GetLastWin32Error());
            CloseHandle(job);
            return null;
        }

        var process = OpenProcess(ProcessSetQuota | ProcessTerminate, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            failure = OwnedInstanceGuard.FailureText("OpenProcess", Marshal.GetLastWin32Error());
            CloseHandle(job);
            return null;
        }

        try
        {
            if (!AssignProcessToJobObject(job, process))
            {
                failure = OwnedInstanceGuard.FailureText("AssignProcessToJobObject", Marshal.GetLastWin32Error());
                CloseHandle(job);
                return null;
            }
        }
        finally
        {
            CloseHandle(process);
        }

        return new OwnedProcessJob(job);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
        ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
