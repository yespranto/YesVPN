using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace YesVPN.Core.Ipc;

/// <summary>
/// Builds restrictive ACLs for the YesVPN named pipe so only local interactive users,
/// LocalSystem, Administrators, and the current owner can connect.
/// </summary>
public static class IpcPipeAcl
{
    public static PipeSecurity CreateLocalInteractivePipeSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Restricts user access to locally logged-on interactive sessions (blocks network logons)
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        return security;
    }
}
