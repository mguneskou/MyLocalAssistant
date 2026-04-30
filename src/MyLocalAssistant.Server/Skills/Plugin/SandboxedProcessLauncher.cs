using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyLocalAssistant.Server.Skills.Plugin;

/// <summary>
/// Spawns a plug-in process attached to a Windows Job Object so it dies when the server
/// dies (or when the wrapping <see cref="SandboxedProcess"/> is disposed). Also imposes
/// a hard memory cap and a single-process limit so the plug-in can't fork.
/// </summary>
/// <remarks>
/// What this DOES enforce:
///  - kill-on-job-close (server crash takes the plug-in with it);
///  - die-on-unhandled-exception (no Watson dialog stalls);
///  - active process limit = 1 (the plug-in cannot Process.Start);
///  - process memory limit (default 512 MB);
///  - working directory pinned to the conversation output folder.
/// What this DOES NOT yet enforce (deferred, all need significant Win32 work):
///  - restricted token / dropped privileges;
///  - filesystem ACL containment;
///  - network blocking (would require WFP filters or AppContainer).
/// Treat plug-ins as semi-trusted: signature verification is the primary gate.
/// </remarks>
public static class SandboxedProcessLauncher
{
    public static SandboxedProcess Launch(
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        long memoryLimitBytes = 512L * 1024 * 1024,
        ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Plug-in sandbox requires Windows.");
        Directory.CreateDirectory(workingDirectory);

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
                        | NativeMethods.JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                    ActiveProcessLimit = 1,
                },
                ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimitBytes,
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
            return new SandboxedProcess(proc, jobHandle);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            if (jobHandle != IntPtr.Zero) NativeMethods.CloseHandle(jobHandle);
            throw;
        }
    }

    internal static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
        public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        public enum JOBOBJECTINFOCLASS
        {
            ExtendedLimitInformation = 9,
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
