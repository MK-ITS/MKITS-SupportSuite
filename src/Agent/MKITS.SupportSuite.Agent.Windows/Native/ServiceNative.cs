// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MKITS.SupportSuite.Agent.Windows.Native;

/// <summary>Service control manager, desktops, tokens and the secure attention sequence (advapi32, user32, wtsapi32, sas).</summary>
internal static partial class ServiceNative
{
    internal const uint ScManagerAllAccess = 0xF003F;
    internal const uint ServiceAllAccess = 0xF01FF;
    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceDemandStart = 0x00000003;
    internal const uint ServiceErrorNormal = 0x00000001;
    internal const uint Delete = 0x00010000;

    internal const uint MaximumAllowed = 0x02000000;
    internal const uint TokenAllAccess = 0xF01FF;
    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;
    internal const int TokenSessionIdClass = 12;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateNoWindow = 0x08000000;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateService(
        nint manager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType,
        uint errorControl, string binaryPathName, string? loadOrderGroup, nint tagId, string? dependencies,
        string? serviceStartName, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenService(nint manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartService(nint service, int argumentCount, nint arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteService(nint service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType,
        out SafeAccessTokenHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref uint information, int length);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string? applicationName,
        [In, Out] char[] commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref ConPtyNative.StartupInfo startupInfo,
        out ConPtyNative.ProcessInformation processInformation);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadDesktop(nint desktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseDesktop(nint desktop);

    internal const uint MoveFileDelayUntilReboot = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

    /// <summary>Raises Ctrl+Alt+Del. Only works from a LocalSystem service (or with the SoftwareSASGeneration policy).</summary>
    [LibraryImport("sas.dll")]
    internal static partial void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
