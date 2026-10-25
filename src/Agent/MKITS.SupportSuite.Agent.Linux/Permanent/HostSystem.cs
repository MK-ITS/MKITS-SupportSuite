// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>A local account (<c>getpwuid_r</c>, so LDAP/SSSD users work too).</summary>
internal sealed record UserAccount(uint Uid, uint Gid, string Name, string Home);

/// <summary>Users, processes and files as seen by the root service. Everything read from user processes is untrusted.</summary>
internal static class HostSystem
{
    private const int StatxModeOffset = 28;
    private const int StatxUidOffset = 20;
    private const int StatxSizeOffset = 40;
    private const ushort FileTypeMask = 0xF000;
    private const ushort RegularFile = 0x8000;
    private const int MaxEnvironSize = 256 * 1024;

    public static bool IsRoot => LibcNative.GetEffectiveUserId() == 0;

    public static unsafe UserAccount? GetUser(uint uid)
    {
        const int BufferSize = 16 * 1024;
        var buffer = NativeMemory.Alloc(BufferSize);
        try
        {
            if (LibcNative.GetPasswordEntry(uid, out var entry, (nint)buffer, BufferSize, out var result) != 0 || result == 0)
            {
                return null;
            }

            var name = Marshal.PtrToStringUTF8(entry.Name);
            var home = Marshal.PtrToStringUTF8(entry.Directory);
            return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(home) ? null : new UserAccount(entry.Uid, entry.Gid, name, home);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>
    /// Reads a regular file only if its owner is one of <paramref name="owners"/> (checked on the opened file, so a
    /// symlink or a file swapped in between cannot redirect root to someone else's file). Never blocks on FIFOs.
    /// </summary>
    public static byte[]? ReadOwnedFile(string path, IReadOnlyCollection<uint> owners, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var fd = LibcNative.Open(path, LibcNative.ReadOnly | LibcNative.NonBlocking | LibcNative.CloseOnExec);
        if (fd < 0)
        {
            return null;
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        var stat = new byte[256];
        if (LibcNative.Statx(fd, string.Empty, LibcNative.AtEmptyPath, LibcNative.StatxBasicStats, stat) != 0)
        {
            return null;
        }

        var mode = BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(StatxModeOffset));
        var owner = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(StatxUidOffset));
        var size = BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(StatxSizeOffset));
        if ((mode & FileTypeMask) != RegularFile || !owners.Contains(owner) || size > (ulong)maxSize)
        {
            return null;
        }

        var data = new byte[(int)size];
        var read = 0;
        while (read < data.Length)
        {
            var n = RandomAccess.Read(handle, data.AsSpan(read), read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return read == data.Length ? data : data[..read];
    }

    /// <summary>Process ids from <c>/proc</c>.</summary>
    public static IEnumerable<int> ProcessIds()
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories("/proc").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                yield return pid;
            }
        }
    }

    /// <summary>The real user id of a process (<c>Uid:</c> line of <c>/proc/&lt;pid&gt;/status</c>).</summary>
    public static uint? ProcessUid(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return fields.Length > 0 && uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // process gone
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string>? ProcessEnvironment(int pid) =>
        ReadProcFile($"/proc/{pid}/environ") is { } data ? ParseEnvironment(data) : null;

    public static IReadOnlyList<string>? ProcessArguments(int pid) =>
        ReadProcFile($"/proc/{pid}/cmdline") is { } data ? ParseArguments(data) : null;

    /// <summary><c>KEY=value\0…</c> (<c>/proc/&lt;pid&gt;/environ</c>); the first value of a key wins.</summary>
    internal static IReadOnlyDictionary<string, string> ParseEnvironment(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in ParseArguments(data))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                result.TryAdd(part[..equals], part[(equals + 1)..]);
            }
        }

        return result;
    }

    /// <summary><c>arg\0…</c> (<c>/proc/&lt;pid&gt;/cmdline</c>).</summary>
    internal static IReadOnlyList<string> ParseArguments(ReadOnlySpan<byte> data) =>
        Encoding.UTF8.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static byte[]? ReadProcFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0);
            var buffer = new byte[MaxEnvironSize];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return buffer[..total];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
