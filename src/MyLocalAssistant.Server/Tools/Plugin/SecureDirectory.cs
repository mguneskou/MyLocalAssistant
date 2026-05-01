using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MyLocalAssistant.Server.Tools.Plugin;

/// <summary>
/// Filesystem helpers for plug-in directories. The goal is "the plug-in process can only
/// see what we wrote into its work-dir". On Windows this means stripping inherited ACEs
/// and granting Full Control only to the server's identity (and SYSTEM, so admins can
/// still clean up). Network/registry/etc. containment is out of scope here.
/// </summary>
public static class SecureDirectory
{
    /// <summary>Create <paramref name="path"/> if missing, then replace its DACL with one
    /// granting Full Control only to the current user and SYSTEM. Inheritance is disabled.
    /// On non-Windows this just ensures the directory exists.</summary>
    public static void EnsureLockedDown(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) return;
        ApplyDaclWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDaclWindows(string path)
    {
        var info = new DirectoryInfo(path);
        var sec = info.GetAccessControl();
        // Strip inherited entries and any explicit ACEs.
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            sec.RemoveAccessRuleSpecific(rule);
        var inheritAll = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        // Server identity (whoever is running the host).
        sec.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            inheritAll,
            PropagationFlags.None,
            AccessControlType.Allow));
        // SYSTEM, so the OS can manage / so admins can later clean up.
        sec.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritAll,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(sec);
    }
}
