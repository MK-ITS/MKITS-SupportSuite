// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Core.Features;
using static MKITS.SupportSuite.Agent.Windows.Native.ConPtyNative;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>PowerShell and cmd in a pseudo console (ConPTY), with the rights of the current user.</summary>
public sealed class ConPtyShellHost : IShellHost
{
    private static readonly Dictionary<string, string> CommandLines = new(StringComparer.Ordinal)
    {
        ["powershell"] = "powershell.exe -NoLogo",
        ["cmd"] = "cmd.exe",
    };

    public IReadOnlyList<string> AvailableShells { get; } = ["powershell", "cmd"];

    public IShellProcess Start(string type, int columns, int rows) =>
        CommandLines.TryGetValue(type, out var commandLine)
            ? ConPtyShell.Start(commandLine, columns, rows)
            : throw new InvalidOperationException($"Unknown shell type '{type}'.");
}

/// <summary>One process attached to a pseudo console. Output and input are anonymous pipes.</summary>
internal sealed class ConPtyShell : IShellProcess
{
    private readonly nint _pseudoConsole;
    private readonly SafeProcessHandle _process;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ProcessWaitHandle _waitHandle;
    private int _disposed;

    private ConPtyShell(nint pseudoConsole, SafeProcessHandle process, FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _process = process;
        _input = input;
        _output = output;
        _waitHandle = new ProcessWaitHandle(process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => OnExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public Stream Output => _output;

    public Task<int> Exited => _exited.Task;

    public static ConPtyShell Start(string commandLine, int columns, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0) || !CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var size = new Coord { X = (short)columns, Y = (short)rows };
        var result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole);
        // The pseudo console holds its own references to these ends.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (result != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new Win32Exception(result);
        }

        nint attributeSize = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeSize);
        var attributeList = Marshal.AllocHGlobal(attributeSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize)
                || !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole, pseudoConsole, nint.Size, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startupInfo = new StartupInfoEx { AttributeList = attributeList };
            startupInfo.StartupInfo.Cb = Marshal.SizeOf<StartupInfoEx>();

            // CreateProcessW may modify the command line buffer: pass a mutable, NUL-terminated copy.
            var commandBuffer = (commandLine + '\0').ToCharArray();
            if (!CreateProcess(null, commandBuffer, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, 0,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref startupInfo, out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _ = CloseHandle(processInfo.Thread);
            return new ConPtyShell(
                pseudoConsole,
                new SafeProcessHandle(processInfo.Process, ownsHandle: true),
                new FileStream(inputWrite, FileAccess.Write, 1),
                new FileStream(outputRead, FileAccess.Read, 1));
        }
        catch
        {
            ClosePseudoConsole(pseudoConsole);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        await _input.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows) =>
        _ = ResizePseudoConsole(_pseudoConsole, new Coord { X = (short)columns, Y = (short)rows });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!_exited.Task.IsCompleted)
        {
            _ = TerminateProcess(_process, 1);
        }

        await _input.DisposeAsync().ConfigureAwait(false);

        // ClosePseudoConsole can block until the output pipe is drained (older Windows builds): close it in the background.
        try
        {
            await Task.Run(() => ClosePseudoConsole(_pseudoConsole)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // the pseudo console is released when the process exits
        }

        await _output.DisposeAsync().ConfigureAwait(false);
        _exitWait.Unregister(null);
        _waitHandle.Dispose();
        _process.Dispose();
    }

    private void OnExited()
    {
        _exited.TrySetResult(GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1);
    }

    /// <summary>Lets the thread pool wait for the process handle.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            var duplicate = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
            SafeWaitHandle = duplicate;
        }
    }
}
