// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Windows.Platform;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>Handles of an elevated shell, valid in the client process (duplicated there by the helper).</summary>
internal readonly record struct HelperShellHandles(int Id, long Input, long Output, long Process);

/// <summary>Elevated shells started by the helper on behalf of its client (ADR-0028).</summary>
internal interface IHelperShells : IDisposable
{
    IReadOnlyList<string> Types { get; }

    /// <summary>Starts a shell and hands its pipes and process to the client; <c>null</c> if that failed.</summary>
    HelperShellHandles? Start(string type, int columns, int rows);

    void Resize(int id, int columns, int rows);

    void Close(int id);
}

/// <summary>
/// The helper's elevated shells: ConPTY shells running as SYSTEM in the customer's session. The helper keeps the pseudo
/// console (resize, close) and duplicates the input/output pipe ends and a wait-only process handle into the client
/// process, so terminal data flows directly between the client and the shell – never through the helper's request
/// pipe. At most <see cref="MaxShells"/> at a time.
/// </summary>
internal sealed partial class HelperShells(int clientProcessId) : IHelperShells
{
    internal const int MaxShells = 6;

    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint Synchronize = 0x00100000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DuplicateSameAccess = 0x2;

    private readonly Dictionary<int, ConPtyShell> _shells = [];
    private int _nextId;

    public IReadOnlyList<string> Types { get; } = ["powershell", "cmd"];

    public HelperShellHandles? Start(string type, int columns, int rows)
    {
        if (!ConPtyShellHost.CommandLines.TryGetValue(type, out var commandLine) || _shells.Count >= MaxShells)
        {
            return null;
        }

        using var client = OpenProcess(ProcessDuplicateHandle, false, (uint)clientProcessId);
        if (client.IsInvalid)
        {
            return null;
        }

        ConPtyShell shell;
        try
        {
            shell = ConPtyShell.Start(commandLine, columns, rows);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }

        nint input = 0, output = 0, process = 0;
        if (Duplicate(shell.InputHandle, client, 0, DuplicateSameAccess, out input)
            && Duplicate(shell.OutputHandle, client, 0, DuplicateSameAccess, out output)
            && Duplicate(shell.ProcessHandle, client, Synchronize | ProcessQueryLimitedInformation, 0, out process))
        {
            var id = ++_nextId;
            _shells[id] = shell;
            return new HelperShellHandles(id, input, output, process);
        }

        // Close what already reached the client (DUPLICATE_CLOSE_SOURCE on its side), then the shell itself.
        foreach (var handle in new[] { input, output, process }.Where(h => h != 0))
        {
            _ = DuplicateHandle(client, handle, 0, out _, 0, false, 0x1 /* DUPLICATE_CLOSE_SOURCE */);
        }

        _ = shell.DisposeAsync().AsTask();
        return null;
    }

    public void Resize(int id, int columns, int rows)
    {
        if (_shells.TryGetValue(id, out var shell))
        {
            shell.Resize(columns, rows);
        }
    }

    public void Close(int id)
    {
        if (_shells.Remove(id, out var shell))
        {
            _ = shell.DisposeAsync().AsTask(); // terminates the shell if it still runs
        }
    }

    public void Dispose()
    {
        foreach (var shell in _shells.Values)
        {
            _ = shell.DisposeAsync().AsTask();
        }

        _shells.Clear();
    }

    private static bool Duplicate(SafeHandle source, SafeProcessHandle target, uint access, uint options, out nint duplicate)
    {
        var added = false;
        try
        {
            source.DangerousAddRef(ref added);
            return DuplicateHandle(GetCurrentProcess(), source.DangerousGetHandle(), target.DangerousGetHandle(), out duplicate, access, false, options);
        }
        finally
        {
            if (added)
            {
                source.DangerousRelease();
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    private static bool DuplicateHandle(SafeProcessHandle sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess, bool inheritHandle, uint options) =>
        DuplicateHandle(sourceProcess.DangerousGetHandle(), sourceHandle, targetProcess, out targetHandle, desiredAccess, inheritHandle, options);
}
