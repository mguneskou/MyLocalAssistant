using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyLocalAssistant.Server.Skills.Plugin;

/// <summary>
/// Spawns a plug-in process attached to a Windows Job Object so it dies when the server
/// dies (or when the wrapping <see cref="SandboxedProcess"/> is disposed). Imposes hard
/// memory + CPU caps, blocks the plug-in from forking, and applies UI-handle restrictions
/// so it cannot read the clipboard, send window messages to other processes, or hook the
/// desktop.
/// </summary>
/// <remarks>
/// THREAT MODEL: a signed plug-in is semi-trusted code. The publisher's private key is the
/// primary authentication; the sandbox is defense-in-depth.
///
/// Implemented:
///  - kill-on-job-close (server crash takes the plug-in with it);
///  - die-on-unhandled-exception (no Watson dialog stalls);
///  - active-process limit = 1 (the plug-in cannot Process.Start);
///  - process memory limit (default 512 MB) and job memory limit;
///  - per-job CPU time cap (default 60 s of user-mode CPU);
///  - UI restrictions: no clipboard, no global atoms, no SendMessage to other windows,
///    no read/write of foreign handles, no SystemParameters changes, no desktop switch;
///  - working directory pinned + ACL'd to current user + SYSTEM (see <see cref="SecureDirectory"/>).
///
/// NOT YET implemented (see notes in code; deliberately deferred):
///  - Restricted token / lowered integrity level. To do this safely we'd have to spawn the
///    process suspended (CreateProcessW with CREATE_SUSPENDED, which .NET's Process.Start
///    does not expose), open the primary token, set the IL to Low (S-1-16-4096) via
///    SetTokenInformation(TokenIntegrityLevel, ...), then ResumeThread. That requires either
///    a PInvoke rewrite of the launcher or a small C++/CLI helper. Until then, the plug-in
///    runs at the server's IL/integrity, which means it inherits the server's filesystem
///    rights inside its working directory but is blocked from elevation by Job + UI limits.
///  - Network containment. Real network sandboxing on Windows requires either AppContainer
///    (which would also bring filesystem virtualization, but breaks .NET's stdio assumptions)
///    or Windows Filtering Platform (WFP) filters keyed by PID, which need a separately
///    installed driver/service. Both are out of scope for v2.1. Operators who must block
///    network access for plug-ins should run the server inside a Windows container or VM
///    with no NIC, or use Windows Defender Application Control / firewall rules pinned to
///    the plug-in executable path.
/// </remarks>
public static class SandboxedProcessLauncher
{
    public static SandboxedProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        long memoryLimitBytes = 512L * 1024 * 1024,
        TimeSpan? cpuLimit = null,
        ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Plug-in sandbox requires Windows.");
        SecureDirectory.EnsureLockedDown(workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start plug-in process '{executable}'.");

        var jobHandle = IntPtr.Zero;
        try
        {
            jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero)
                throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags =
                        NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        | NativeMethods.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION
                        | NativeMethods.JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                        | NativeMethods.JOB_OBJECT_LIMIT_PROCESS_MEMORY
                        | NativeMethods.JOB_OBJECT_LIMIT_JOB_MEMORY
                        | NativeMethods.JOB_OBJECT_LIMIT_JOB_TIME,
                    ActiveProcessLimit = 1,
                    PerJobUserTimeLimit = (cpuLimit ?? TimeSpan.FromSeconds(60)).Ticks,
                },
                ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes,
                JobMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes,
            };
            var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!NativeMethods.SetInformationJobObject(jobHandle,
                        NativeMethods.JOBOBJECTINFOCLASS.ExtendedLimitInformation, ptr, (uint)size))
                    throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");
            }
            finally { Marshal.FreeHGlobal(ptr); }

            if (!NativeMethods.AssignProcessToJobObject(jobHandle, proc.Handle))
            {
                var err = Marshal.GetLastWin32Error();
                // ERROR_ACCESS_DENIED (5) typically means the process is already in a job
                // (e.g. Visual Studio's debug job) that doesn't allow nested jobs. Fall back
                // to lifetime-bound monitoring rather than refusing to launch in dev.
                if (err != 5)
                    throw new InvalidOperationException($"AssignProcessToJobObject failed: {err}");
                log?.LogWarning("Could not place plug-in {Pid} in a Job Object (err=5); running unsandboxed. This is expected under a debugger.", proc.Id);
                NativeMethods.CloseHandle(jobHandle);
                jobHandle = IntPtr.Zero;
            }
            else
            {
                ApplyUiRestrictions(jobHandle, log);
            }
            return new SandboxedProcess(proc, jobHandle);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            if (jobHandle != IntPtr.Zero) NativeMethods.CloseHandle(jobHandle);
            throw;
        }
    }

    private static void ApplyUiRestrictions(IntPtr jobHandle, ILogger? log)
    {
        var ui = new NativeMethods.JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass =
                NativeMethods.JOB_OBJECT_UILIMIT_HANDLES
                | NativeMethods.JOB_OBJECT_UILIMIT_READCLIPBOARD
                | NativeMethods.JOB_OBJECT_UILIMIT_WRITECLIPBOARD
                | NativeMethods.JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS
                | NativeMethods.JOB_OBJECT_UILIMIT_DISPLAYSETTINGS
                | NativeMethods.JOB_OBJECT_UILIMIT_GLOBALATOMS
                | NativeMethods.JOB_OBJECT_UILIMIT_DESKTOP
                | NativeMethods.JOB_OBJECT_UILIMIT_EXITWINDOWS,
        };
        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_BASIC_UI_RESTRICTIONS>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(ui, ptr, false);
            if (!NativeMethods.SetInformationJobObject(jobHandle,
                    NativeMethods.JOBOBJECTINFOCLASS.BasicUIRestrictions, ptr, (uint)size))
                log?.LogWarning("SetInformationJobObject(BasicUIRestrictions) failed: {Err}", Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    internal static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
        public const uint JOB_OBJECT_LIMIT_JOB_TIME = 0x00000004;

        // JOBOBJECT_BASIC_UI_RESTRICTIONS.UIRestrictionsClass bits.
        public const uint JOB_OBJECT_UILIMIT_HANDLES         = 0x00000001;
        public const uint JOB_OBJECT_UILIMIT_READCLIPBOARD   = 0x00000002;
        public const uint JOB_OBJECT_UILIMIT_WRITECLIPBOARD  = 0x00000004;
        public const uint JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS = 0x00000008;
        public const uint JOB_OBJECT_UILIMIT_DISPLAYSETTINGS = 0x00000010;
        public const uint JOB_OBJECT_UILIMIT_GLOBALATOMS    = 0x00000020;
        public const uint JOB_OBJECT_UILIMIT_DESKTOP        = 0x00000040;
        public const uint JOB_OBJECT_UILIMIT_EXITWINDOWS    = 0x00000080;

        public enum JOBOBJECTINFOCLASS
        {
            BasicUIRestrictions = 4,
            ExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            public uint UIRestrictionsClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>
/// Owns the underlying <see cref="Process"/> and Job Object. Disposing closes the job,
/// which kills the plug-in process tree.
/// </summary>
public sealed class SandboxedProcess : IDisposable
{
    public Process Process { get; }
    private IntPtr _jobHandle;
    private bool _disposed;

    internal SandboxedProcess(Process process, IntPtr jobHandle)
    {
        Process = process;
        _jobHandle = jobHandle;
    }

    public Stream StandardInput => Process.StandardInput.BaseStream;
    public Stream StandardOutput => Process.StandardOutput.BaseStream;
    public StreamReader StandardError => Process.StandardError;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_jobHandle != IntPtr.Zero)
            {
                SandboxedProcessLauncher.NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
            if (!Process.HasExited)
            {
                try { Process.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally
        {
            Process.Dispose();
        }
    }
}
