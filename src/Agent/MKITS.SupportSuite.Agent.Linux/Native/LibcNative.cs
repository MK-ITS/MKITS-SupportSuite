// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Linux.Native;

/// <summary>glibc functions for pseudo terminals, process spawning, users and files (libc.so.6).</summary>
internal static partial class LibcNative
{
    private const string Libc = "libc.so.6";

    internal const int ReadWrite = 0x2;     // O_RDWR
    internal const int NoControllingTerminal = 0x100; // O_NOCTTY
    internal const int CloseOnExec = 0x80000; // O_CLOEXEC
    internal const int NonBlocking = 0x800;   // O_NONBLOCK (x86_64 and arm64)
    internal const int ReadOnly = 0x0;        // O_RDONLY
    internal const int AtEmptyPath = 0x1000;  // AT_EMPTY_PATH
    internal const uint StatxBasicStats = 0x7FF; // STATX_BASIC_STATS
    internal const ulong SetWindowSize = 0x5414; // TIOCSWINSZ (x86_64 and arm64)
    internal const int SignalHangUp = 1;
    internal const int SignalKill = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort XPixels;
        public ushort YPixels;
    }

    /// <summary><c>struct passwd</c> (LP64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Passwd
    {
        public nint Name;
        public nint Password;
        public uint Uid;
        public uint Gid;
        public nint Gecos;
        public nint Directory;
        public nint Shell;
    }

    [LibraryImport(Libc, EntryPoint = "posix_openpt", SetLastError = true)]
    internal static partial int PosixOpenPt(int flags);

    [LibraryImport(Libc, EntryPoint = "grantpt", SetLastError = true)]
    internal static partial int GrantPt(int fd);

    [LibraryImport(Libc, EntryPoint = "unlockpt", SetLastError = true)]
    internal static partial int UnlockPt(int fd);

    [LibraryImport(Libc, EntryPoint = "ptsname_r", SetLastError = true)]
    internal static partial int PtsNameR(int fd, byte[] buffer, nuint length);

    [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int IoctlWindowSize(int fd, ulong request, ref WindowSize size);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_init")]
    internal static partial int FileActionsInit(nint actions);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_adddup2")]
    internal static partial int FileActionsAddDup2(nint actions, int fd, int newFd);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_addclose")]
    internal static partial int FileActionsAddClose(nint actions, int fd);

    [LibraryImport(Libc, EntryPoint = "posix_spawn_file_actions_destroy")]
    internal static partial int FileActionsDestroy(nint actions);

    [LibraryImport(Libc, EntryPoint = "posix_spawn", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int PosixSpawn(out int pid, string path, nint fileActions, nint attributes, nint argv, nint envp);

    [LibraryImport(Libc, EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitPid(int pid, out int status, int options);

    [LibraryImport(Libc, EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int signal);

    [LibraryImport(Libc, EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();

    [LibraryImport(Libc, EntryPoint = "getpwuid_r")]
    internal static partial int GetPasswordEntry(uint uid, out Passwd entry, nint buffer, nuint length, out nint result);

    /// <summary><c>statx</c> (glibc ≥ 2.28) – one struct layout on all architectures, unlike <c>stat</c>.</summary>
    [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Statx(int directoryFd, string path, int flags, uint mask, byte[] buffer);

    [LibraryImport(Libc, EntryPoint = "dup", SetLastError = true)]
    internal static partial int Dup(int fd);

    [LibraryImport(Libc, EntryPoint = "dup2", SetLastError = true)]
    internal static partial int Dup2(int fd, int newFd);
}
