using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Mike.Common;

/// <summary>
/// Creates local IPC endpoints with an explicit Windows ACL.  The default
/// NamedPipeServerStream ACL is not a sufficient authorization boundary for a
/// service that can execute tools.
/// </summary>
public static class NamedPipeSecurity
{
    public static NamedPipeServerStream CreateServer(string pipeName, bool administrativeOnly = false, bool currentUserOnly = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mike local IPC requires Windows named-pipe ACLs.");

        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentUser = currentIdentity.User;

        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administrators, PipeAccessRights.FullControl, AccessControlType.Allow));
        // The account hosting the server needs CreateNewInstance/permission
        // rights as well as read/write access. Without this, the first pipe
        // instance can work but subsequent instances fail with access denied
        // when Mike runs interactively during development.
        if (currentUser is not null)
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (!administrativeOnly && !currentUserOnly)
        {
            // The desktop app runs in the logged-on user's session. Interactive
            // users can use the normal pipe; privileged operations still require
            // the Action Guard and are never authorized by the ACL alone.
            var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
            security.AddAccessRule(new PipeAccessRule(interactive, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 10,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    public static bool IsAdministrator(NamedPipeServerStream server)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var result = false;
#pragma warning disable CA1416 // The runtime guard above is outside this callback.
        server.RunAsClient(() => result = IsCurrentIdentityAdministrator());
#pragma warning restore CA1416
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentIdentityAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is not null &&
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
