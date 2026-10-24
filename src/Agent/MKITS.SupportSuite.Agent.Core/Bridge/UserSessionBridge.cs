// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Bridge;

/// <summary>A connection to the agent's process in the logged-on user's session. Disposing ends that process.</summary>
public interface IUserSessionConnection : IAsyncDisposable
{
    Stream Stream { get; }
}

/// <summary>Starts the user-session process and connects to it (platform-specific).</summary>
public interface IUserSessionConnector
{
    /// <summary>Returns <c>null</c> when nobody is logged on or the process could not be started.</summary>
    Task<IUserSessionConnection?> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Service side of the split agent (Windows service / Linux system service): forwards the channels that need the
/// logged-on user – chat, clipboard, files, shell – to a process in the user's session, which runs the normal channel
/// handlers with the <b>user's</b> rights, and lets that process show the consent dialog and the session window.
/// The connection, the device key and the end-to-end keys stay in the service. Without a user session (nobody logged
/// on) these channels are not available and the <c>fallback</c> UI handles consent and notices.
/// </summary>
public sealed class UserSessionBridge(IUserSessionConnector connector, IAgentUserInterface fallback) : ISessionChannelHandler, IAgentUserInterface, IAsyncDisposable
{
    /// <summary>How long a consent request waits for the user-session process to come up.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private static readonly HashSet<Channel> ForwardedChannels = [Channel.Chat, Channel.Clipboard, Channel.File, Channel.Shell];

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _consents = new();
    private readonly CancellationTokenSource _disposed = new();
    private Task<IUserSessionConnection?>? _starting;
    private IUserSessionConnection? _connection;
    private ISessionContext? _context;
    private int _nextConsentId;

    // Last known state, sent to a user-session process that connects late.
    private (string Operator, ConsentPolicy Policy)? _assigned;
    private (AgentStatus Status, string? Detail)? _status;
    private IReadOnlyList<string> _supporters = [];

    /// <summary>The user clicked "End session" in the session window.</summary>
    public event EventHandler? EndSessionRequested;

    /// <summary>Whether a user-session process is connected right now.</summary>
    public bool IsConnected => Volatile.Read(ref _connection) is not null;

    // ------------------------------------------------------------------ IAgentUserInterface (called by the session engine)

    public void OnSessionAssigned(string operatorName, ConsentPolicy policy)
    {
        fallback.OnSessionAssigned(operatorName, policy);
        _assigned = (operatorName, policy);
        _status = null;
        _supporters = [];
        _ = StartAsync(); // the window (if any) and the user-rights handlers come up while the supporter connects
        _ = SendAsync(AssignedMessage(operatorName, policy));
    }

    public void OnStatusChanged(AgentStatus status, string? detail)
    {
        fallback.OnStatusChanged(status, detail);
        if (status is AgentStatus.Idle or AgentStatus.Reconnecting or AgentStatus.Ended)
        {
            _ = StopAsync(); // no session: no process in the user's session
            return;
        }

        _status = (status, detail);
        _ = SendAsync(StatusMessage(status, detail));
    }

    public void OnSupportersChanged(IReadOnlyList<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        if (!IsConnected)
        {
            fallback.OnSupportersChanged(displayNames); // e.g. the "notify" message box
        }

        _supporters = displayNames.ToList();
        _ = SendAsync(SupportersMessage(_supporters));
    }

    public void OnSecurityCode(string supporterDisplayName, string securityCode)
    {
        fallback.OnSecurityCode(supporterDisplayName, securityCode);
        _ = SendAsync(BridgeProtocol.Build(BridgeMessageType.SecurityCode, w =>
        {
            w.Write(supporterDisplayName);
            w.Write(securityCode);
        }));
    }

    public async Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var connection = await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return await fallback.RequestConsentAsync(prompt, cancellationToken).ConfigureAwait(false);
        }

        var id = Interlocked.Increment(ref _nextConsentId);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _consents[id] = answer;
        try
        {
            await SendAsync(BridgeProtocol.Build(BridgeMessageType.ConsentRequest, w =>
            {
                w.Write(id);
                w.Write(prompt.ServerHost);
                w.Write(prompt.OperatorName);
                w.Write(prompt.SupporterDisplayName);
                BridgeProtocol.WriteStrings(w, prompt.Permissions);
            })).ConfigureAwait(false);
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _consents.TryRemove(id, out _);
        }
    }

    // ------------------------------------------------------------------ ISessionChannelHandler

    public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _context = context;
        if (!ForwardedChannels.Contains(PayloadChannels.For(payload)))
        {
            return Task.CompletedTask;
        }

        return SendAsync(BridgeProtocol.Build(BridgeMessageType.Payload, w =>
        {
            w.Write(viewerId);
            var bytes = payload.ToByteArray();
            w.Write(bytes.Length);
            w.Write(bytes);
        }));
    }

    public Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken)
    {
        _context = context;
        return SendAsync(BridgeProtocol.Build(BridgeMessageType.ViewerReady, w => w.Write(viewerId)));
    }

    public void OnViewerGone(string viewerId) =>
        _ = SendAsync(BridgeProtocol.Build(BridgeMessageType.ViewerGone, w => w.Write(viewerId)));

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        _disposed.Dispose();
        _writeLock.Dispose();
    }

    // ------------------------------------------------------------------ connection

    private Task<IUserSessionConnection?> StartAsync()
    {
        lock (_gate)
        {
            if (_starting is not null)
            {
                return _starting;
            }

            _starting = ConnectAndServeAsync();
            return _starting;
        }
    }

    private async Task<IUserSessionConnection?> ConnectAndServeAsync()
    {
        IUserSessionConnection? connection;
        try
        {
            connection = await connector.ConnectAsync(_disposed.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            connection = null;
        }

        if (connection is null)
        {
            lock (_gate)
            {
                _starting = null; // try again with the next session
            }

            return null;
        }

        Volatile.Write(ref _connection, connection);
        _ = ReadLoopAsync(connection);

        // Catch up: the session may have started while the process was coming up.
        if (_assigned is { } assigned)
        {
            await SendAsync(AssignedMessage(assigned.Operator, assigned.Policy)).ConfigureAwait(false);
        }

        if (_status is { } status)
        {
            await SendAsync(StatusMessage(status.Status, status.Detail)).ConfigureAwait(false);
        }

        await SendAsync(SupportersMessage(_supporters)).ConfigureAwait(false);
        return connection;
    }

    private static byte[] AssignedMessage(string operatorName, ConsentPolicy policy) => BridgeProtocol.Build(BridgeMessageType.SessionAssigned, w =>
    {
        w.Write(operatorName);
        w.Write((int)policy);
    });

    private static byte[] StatusMessage(AgentStatus status, string? detail) => BridgeProtocol.Build(BridgeMessageType.Status, w =>
    {
        w.Write((int)status);
        w.Write(detail ?? string.Empty);
    });

    private static byte[] SupportersMessage(IReadOnlyCollection<string> names) =>
        BridgeProtocol.Build(BridgeMessageType.Supporters, w => BridgeProtocol.WriteStrings(w, names));

    private async Task<IUserSessionConnection?> WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        var starting = StartAsync();
        try
        {
            return await starting.WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task StopAsync()
    {
        IUserSessionConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
            _starting = null;
            _context = null;
        }

        foreach (var consent in _consents.Values)
        {
            consent.TrySetResult(false);
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendAsync(byte[] message)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is null)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(_disposed.Token).ConfigureAwait(false);
            try
            {
                await BridgeProtocol.WriteAsync(connection.Stream, message, _disposed.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // the user-session process is gone; the read loop cleans up
        }
    }

    private async Task ReadLoopAsync(IUserSessionConnection connection)
    {
        try
        {
            while (true)
            {
                var message = await BridgeProtocol.ReadAsync(connection.Stream, _disposed.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await HandleMessageAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
            or InvalidDataException or EndOfStreamException or InvalidProtocolBufferException)
        {
            // closed, or the user-session process sent garbage: drop it
        }

        if (ReferenceEquals(Volatile.Read(ref _connection), connection))
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Messages from the user's session. That process runs with user rights: only these few requests are accepted.</summary>
    private async Task HandleMessageAsync(byte[] message)
    {
        using var reader = new BinaryReader(new MemoryStream(message));
        switch ((BridgeMessageType)reader.ReadByte())
        {
            case BridgeMessageType.Payload:
                var viewerId = BridgeProtocol.ReadString(reader);
                var payload = SessionPayload.Parser.ParseFrom(reader.ReadBytes(Math.Clamp(reader.ReadInt32(), 0, BridgeProtocol.MaxMessageSize)));
                if (ForwardedChannels.Contains(PayloadChannels.For(payload)) && _context is { } context)
                {
                    await context.SendAsync(viewerId.Length == 0 ? null : viewerId, payload, _disposed.Token).ConfigureAwait(false);
                }

                break;
            case BridgeMessageType.ConsentAnswer:
                var id = reader.ReadInt32();
                var granted = reader.ReadBoolean();
                if (_consents.TryGetValue(id, out var answer))
                {
                    answer.TrySetResult(granted);
                }

                break;
            case BridgeMessageType.EndSession:
                EndSessionRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                throw new InvalidDataException("Unexpected message from the user session.");
        }
    }
}
