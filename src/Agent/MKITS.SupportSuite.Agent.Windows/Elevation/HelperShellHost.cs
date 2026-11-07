// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Platform;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>
/// Elevated shells of the ad-hoc client (ADR-0028): started by the SYSTEM helper while administrator mode is on. Without
/// a connected helper, starting one fails and the viewer sees "refused".
/// </summary>
internal sealed class HelperShellHost(Func<HelperClient?> helper) : IShellHost
{
    public IReadOnlyList<string> AvailableShells { get; } = ["powershell", "cmd"];

    public IShellProcess Start(string type, int columns, int rows) =>
        helper() is { IsBroken: false } client && client.StartShell(type, columns, rows) is { } shell
            ? shell
            : throw new InvalidOperationException("Administrator mode is not active.");
}

/// <summary>An elevated shell in the helper, driven through the pipe and process handles the helper duplicated into this process.</summary>
internal sealed class HelperShellProcess : IShellProcess
{
    private readonly HelperClient _client;
    private readonly int _id;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly SafeProcessHandle _process;
    private readonly ConPtyShell.ProcessWaitHandle _waitHandle;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public HelperShellProcess(HelperClient client, HelperShellHandles handles)
    {
        _client = client;
        _id = handles.Id;
        _input = new FileStream(new SafeFileHandle((nint)handles.Input, ownsHandle: true), FileAccess.Write, 1);
        _output = new FileStream(new SafeFileHandle((nint)handles.Output, ownsHandle: true), FileAccess.Read, 1);
        _process = new SafeProcessHandle((nint)handles.Process, ownsHandle: true);
        _waitHandle = new ConPtyShell.ProcessWaitHandle(_process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => _exited.TrySetResult(ConPtyShell.ExitCode(_process)), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public Stream Output => _output;

    public Task<int> Exited => _exited.Task;

    public async Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        await _input.WriteAsync(input, cancellationToken).ConfigureAwait(false);
        await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows) => _client.ResizeShell(_id, columns, rows);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.CloseShell(_id); // the helper terminates the shell and closes its pseudo console
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        ConPtyShell.UnregisterAndWait(_exitWait); // an exit callback may be running: let it finish before the handle goes
        _waitHandle.Dispose();
        _process.Dispose();
    }
}
