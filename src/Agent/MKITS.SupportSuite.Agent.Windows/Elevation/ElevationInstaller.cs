// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using static MKITS.SupportSuite.Agent.Windows.Native.ServiceNative;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>
/// <c>--elevate &lt;id&gt; &lt;clientPid&gt; &lt;userSid&gt;</c>: started by the client through a UAC prompt. Copies the EXE into
/// a protected folder, records the session parameters and installs + starts the temporary LocalSystem service.
/// It verifies that the client process really belongs to the given account, so a pipe can never be opened for
/// somebody else.
/// </summary>
internal static class ElevationInstaller
{
    public static int Run(string[] args)
    {
        if (args.Length < 4 || !ElevatedSessionConfig.IsValidId(args[1])
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var clientPid))
        {
            return 10;
        }

        var id = args[1];
        SecurityIdentifier userSid;
        try
        {
            userSid = new SecurityIdentifier(args[3]);
        }
        catch (ArgumentException)
        {
            return 11;
        }

        if (!ProcessBelongsTo(clientPid, userSid))
        {
            return 12;
        }

        var config = new ElevatedSessionConfig(id, HelperClient.PipeNameFor(id), clientPid, userSid.Value);
        config.Save();
        File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("No process path."), ElevatedSessionConfig.ExecutableFor(id), overwrite: false);

        var manager = OpenSCManager(null, null, ScManagerAllAccess);
        if (manager == 0)
        {
            return 13;
        }

        try
        {
            var exe = ElevatedSessionConfig.ExecutableFor(id);
            var service = CreateService(
                manager, config.ServiceName, "MKITS Remote Support (temporary)", ServiceAllAccess, ServiceWin32OwnProcess,
                ServiceDemandStart, ServiceErrorNormal, $"\"{exe}\" --service {id}", null, 0, null, null, null);
            if (service == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                if (!StartService(service, 0, 0))
                {
                    _ = DeleteService(service);
                    return 14;
                }
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }

        return 0;
    }

    private static bool ProcessBelongsTo(int processId, SecurityIdentifier expected)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!OpenProcessToken(process.Handle, 0x0008 /* TOKEN_QUERY */, out var token))
            {
                return false;
            }

            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                return identity.User == expected;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
