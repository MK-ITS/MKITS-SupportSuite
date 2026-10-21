// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Windows.Native;
using static MKITS.SupportSuite.Agent.Windows.Native.ServiceNative;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>
/// Starts a process as SYSTEM in the active console session on the interactive desktop (service only): the
/// service's own token is duplicated and moved into the console session.
/// </summary>
internal static class ConsoleSessionLauncher
{
    public const uint NoSession = 0xFFFFFFFF;

    /// <summary>The session shown on the physical console, or <see cref="NoSession"/> during a session switch.</summary>
    public static uint ActiveSessionId => WTSGetActiveConsoleSessionId();

    /// <summary>Returns the process and the session it runs in, or <c>null</c>.</summary>
    public static (Process Process, uint SessionId)? Start(string exe, string arguments)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == NoSession)
        {
            return null;
        }

        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAllAccess, out var own))
        {
            return null;
        }

        using (own)
        {
            if (!DuplicateTokenEx(own, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out var token))
            {
                return null;
            }

            using (token)
            {
                if (!SetTokenInformation(token, TokenSessionIdClass, ref sessionId, sizeof(uint)))
                {
                    return null;
                }

                var desktop = Marshal.StringToHGlobalUni("winsta0\\default");
                try
                {
                    var startup = new ConPtyNative.StartupInfo { Cb = Marshal.SizeOf<ConPtyNative.StartupInfo>(), Desktop = desktop };
                    var commandLine = $"\"{exe}\" {arguments}\0".ToCharArray();
                    if (!CreateProcessAsUser(token, exe, commandLine, 0, 0, false, CreateUnicodeEnvironment | CreateNoWindow, 0,
                            Path.GetDirectoryName(exe), ref startup, out var info))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }

                    _ = ConPtyNative.CloseHandle(info.Thread);
                    _ = ConPtyNative.CloseHandle(info.Process);
                    return (Process.GetProcessById(info.ProcessId), sessionId);
                }
                catch (Exception ex) when (ex is Win32Exception or ArgumentException)
                {
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(desktop);
                }
            }
        }
    }
}
