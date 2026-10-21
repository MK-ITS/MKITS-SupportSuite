// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.AccessControl;
using System.Security.Principal;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>Folders only SYSTEM and Administrators can access (no inherited permissions).</summary>
internal static class ProtectedFolder
{
    /// <summary>Creates the folder (and its parents) and replaces its permissions with SYSTEM + Administrators full control.</summary>
    public static void Create(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            directory.SetAccessControl(security);
        }
        else
        {
            directory.Create(security);
        }
    }
}
