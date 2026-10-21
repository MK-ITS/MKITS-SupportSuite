// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>A running shell attached to a pseudo terminal (Windows: ConPTY, Linux: PTY).</summary>
public interface IShellProcess : IAsyncDisposable
{
    /// <summary>Terminal output (UTF-8 with VT sequences). Returns 0 bytes when the shell exited.</summary>
    Stream Output { get; }

    Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);

    void Resize(int columns, int rows);

    /// <summary>Completes with the exit code.</summary>
    Task<int> Exited { get; }
}

/// <summary>Starts shells on the platform.</summary>
public interface IShellHost
{
    /// <summary>Shell types this platform offers, e.g. <c>powershell</c>, <c>cmd</c> or <c>bash</c>, <c>sh</c>. The first is the default.</summary>
    IReadOnlyList<string> AvailableShells { get; }

    /// <summary>Starts a shell with the user rights of the agent process (the ad-hoc client never elevates on its own).</summary>
    IShellProcess Start(string type, int columns, int rows);
}

/// <summary>
/// Remote shell (docs/protocol.md §5, channel SHELL): opens shells on request of a viewer, pipes input and output,
/// reports the exit code and closes all shells of a viewer when it leaves. Only shell types the platform offers can
/// be started – the viewer can never choose an arbitrary program.
/// </summary>
public sealed class ShellHandler(IShellHost host) : ISessionChannelHandler, IAsyncDisposable
{
    internal const int MaxShellsPerViewer = 3;
    internal const int OutputChunkSize = 16 * 1024;
    internal const int MaxInputSize = 64 * 1024;

    private readonly ConcurrentDictionary<string, RunningShell> _shells = new(StringComparer.Ordinal);

    public async Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);

        switch (payload.BodyCase)
        {
            case SessionPayload.BodyOneofCase.ShellOpen:
                await OpenAsync(context, viewerId, payload.ShellOpen, cancellationToken).ConfigureAwait(false);
                break;
            case SessionPayload.BodyOneofCase.ShellData when payload.ShellData.Data.Length <= MaxInputSize:
                if (_shells.TryGetValue(Key(viewerId, payload.ShellData.ShellId), out var target))
                {
                    await target.Process.WriteAsync(payload.ShellData.Data.Memory, cancellationToken).ConfigureAwait(false);
                }

                break;
            case SessionPayload.BodyOneofCase.ShellResize:
                if (_shells.TryGetValue(Key(viewerId, payload.ShellResize.ShellId), out var resized))
                {
                    resized.Process.Resize(Clamp(payload.ShellResize.Cols, 20, 500), Clamp(payload.ShellResize.Rows, 5, 200));
                }

                break;
            case SessionPayload.BodyOneofCase.ShellExit:
                // The viewer closed the terminal: terminate the shell.
                if (_shells.TryRemove(Key(viewerId, payload.ShellExit.ShellId), out var closed))
                {
                    await closed.DisposeAsync().ConfigureAwait(false);
                }

                break;
            default:
                break;
        }
    }

    public void OnViewerGone(string viewerId)
    {
        foreach (var key in _shells.Keys.Where(k => k.StartsWith(viewerId + "/", StringComparison.Ordinal)).ToList())
        {
            if (_shells.TryRemove(key, out var shell))
            {
                _ = shell.DisposeAsync().AsTask();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _shells.Keys.ToList())
        {
            if (_shells.TryRemove(key, out var shell))
            {
                await shell.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task OpenAsync(ISessionContext context, string viewerId, ShellOpen open, CancellationToken cancellationToken)
    {
        var shellId = open.ShellId;
        var type = string.IsNullOrEmpty(open.Type) ? host.AvailableShells.FirstOrDefault() : open.Type;
        var key = Key(viewerId, shellId);
        var perViewer = _shells.Keys.Count(k => k.StartsWith(viewerId + "/", StringComparison.Ordinal));

        if (string.IsNullOrEmpty(shellId) || shellId.Length > 64 || _shells.ContainsKey(key) || perViewer >= MaxShellsPerViewer
            || type is null || !host.AvailableShells.Contains(type, StringComparer.Ordinal))
        {
            await SendExitAsync(context, viewerId, shellId, -1, cancellationToken).ConfigureAwait(false);
            return;
        }

        IShellProcess process;
        try
        {
            process = host.Start(type, Clamp(open.Cols, 20, 500), Clamp(open.Rows, 5, 200));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            await SendExitAsync(context, viewerId, shellId, -1, cancellationToken).ConfigureAwait(false);
            return;
        }

        var shell = new RunningShell(process);
        _shells[key] = shell;
        shell.Pump = PumpAsync(context, viewerId, shellId, shell);
    }

    /// <summary>Forwards output until the shell exits, then reports the exit code.</summary>
    private async Task PumpAsync(ISessionContext context, string viewerId, string shellId, RunningShell shell)
    {
        var buffer = new byte[OutputChunkSize];
        var token = shell.Stopping.Token;
        try
        {
            int read;
            while ((read = await shell.Process.Output.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await context.SendAsync(viewerId, new SessionPayload
                {
                    ShellData = new ShellData { ShellId = shellId, Data = ByteString.CopyFrom(buffer, 0, read) },
                }, token).ConfigureAwait(false);
            }

            var exitCode = await shell.Process.Exited.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            await SendExitAsync(context, viewerId, shellId, exitCode, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            // closed by the viewer or the session ended
        }
        finally
        {
            if (_shells.TryRemove(Key(viewerId, shellId), out var removed) && ReferenceEquals(removed, shell))
            {
                await shell.Process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static Task SendExitAsync(ISessionContext context, string viewerId, string shellId, int exitCode, CancellationToken cancellationToken) =>
        context.SendAsync(viewerId, new SessionPayload { ShellExit = new ShellExit { ShellId = shellId, ExitCode = exitCode } }, cancellationToken);

    private static string Key(string viewerId, string shellId) => viewerId + "/" + shellId;

    private static int Clamp(uint value, int min, int max) => (int)Math.Clamp(value, (uint)min, (uint)max);

    private sealed class RunningShell(IShellProcess process) : IAsyncDisposable
    {
        public IShellProcess Process { get; } = process;

        public CancellationTokenSource Stopping { get; } = new();

        public Task? Pump { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Stopping.CancelAsync().ConfigureAwait(false);
            await Process.DisposeAsync().ConfigureAwait(false);
            if (Pump is { } pump)
            {
                await pump.ConfigureAwait(false);
            }

            Stopping.Dispose();
        }
    }
}
