// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Windows.Native;
using static MKITS.SupportSuite.Agent.Windows.Native.ServiceNative;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>The user logged on at the console, with a primary token to start processes as that user (service only).</summary>
internal sealed class ConsoleUser : IDisposable
{
    private ConsoleUser(SafeAccessTokenHandle token, uint sessionId, SecurityIdentifier sid)
    {
        Token = token;
        SessionId = sessionId;
        Sid = sid;
    }

    public SafeAccessTokenHandle Token { get; }

    public uint SessionId { get; }

    public SecurityIdentifier Sid { get; }

    /// <summary>The user of the active console session, or <c>null</c> if nobody is logged on there.</summary>
    public static ConsoleUser? TryGet()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == ConsoleSessionLauncher.NoSession || !UserSessionNative.WTSQueryUserToken(sessionId, out var impersonation))
        {
            return null; // ERROR_NO_TOKEN: nobody logged on
        }

        using (impersonation)
        {
            if (!DuplicateTokenEx(impersonation, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out var primary))
            {
                return null;
            }

            using var identity = new WindowsIdentity(primary.DangerousGetHandle());
            if (identity.User is not { } sid)
            {
                primary.Dispose();
                return null;
            }

            return new ConsoleUser(primary, sessionId, sid);
        }
    }

    /// <summary>Starts <paramref name="exe"/> as this user on the interactive desktop, with the user's environment.</summary>
    public Process? Start(string exe, string arguments)
    {
        if (!UserSessionNative.CreateEnvironmentBlock(out var environment, Token, false))
        {
            return null;
        }

        var desktop = Marshal.StringToHGlobalUni("winsta0\\default");
        try
        {
            var startup = new ConPtyNative.StartupInfo { Cb = Marshal.SizeOf<ConPtyNative.StartupInfo>(), Desktop = desktop };
            var commandLine = $"\"{exe}\" {arguments}\0".ToCharArray();
            if (!CreateProcessAsUser(Token, exe, commandLine, 0, 0, false, CreateUnicodeEnvironment, environment,
                    Path.GetDirectoryName(exe), ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _ = ConPtyNative.CloseHandle(info.Thread);
            _ = ConPtyNative.CloseHandle(info.Process);
            return Process.GetProcessById(info.ProcessId);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(desktop);
            _ = UserSessionNative.DestroyEnvironmentBlock(environment);
        }
    }

    public void Dispose() => Token.Dispose();
}

internal static partial class UserSessionNative
{
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
