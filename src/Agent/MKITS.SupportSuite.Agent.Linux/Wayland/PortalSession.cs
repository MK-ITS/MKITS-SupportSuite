// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>A screen shared through the portal: PipeWire node and its place on the desktop (logical pixels).</summary>
internal sealed record PortalStream(uint NodeId, int X, int Y, int Width, int Height);

/// <summary>The user declined, or the portal failed (response code 1 = cancelled, 2 = other).</summary>
internal sealed class PortalRequestException(string method, uint response)
    : Exception(string.Create(CultureInfo.InvariantCulture, $"The desktop portal refused {method} (response {response})."))
{
    public uint Response { get; } = response;

    public bool Cancelled => Response == 1;
}

/// <summary>
/// A remote desktop session of xdg-desktop-portal (ADR-0011, Wayland): RemoteDesktop for input plus ScreenCast for the
/// screens, in one session – the desktop shows its own dialog in which the user picks the screens and allows control.
/// Frames come from PipeWire (<see cref="OpenPipeWireRemoteAsync"/>); input goes through the <c>Notify*</c> methods.
/// Portal requests answer with a <c>Response</c> signal on a request object whose path is known in advance; the
/// signal is subscribed to before the call so it cannot be missed.
/// </summary>
internal sealed class PortalSession : IPortalInput, IAsyncDisposable
{
    internal const string Service = "org.freedesktop.portal.Desktop";
    internal const string ObjectPath = "/org/freedesktop/portal/desktop";
    internal const string RemoteDesktopInterface = "org.freedesktop.portal.RemoteDesktop";
    internal const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    internal const string RequestInterface = "org.freedesktop.portal.Request";
    internal const string SessionInterface = "org.freedesktop.portal.Session";

    internal const uint DeviceKeyboard = 1;
    internal const uint DevicePointer = 2;
    internal const uint SourceMonitor = 1;
    internal const uint CursorEmbedded = 2;
    internal const uint PersistUntilRevoked = 2;

    /// <summary>The user may take a while to answer the desktop's dialog.</summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(3);

    private readonly Connection _connection;
    private readonly string _destination;
    private int _closed;

    private PortalSession(Connection connection, string destination, string handle, IReadOnlyList<PortalStream> streams, uint devices, string? restoreToken)
    {
        _connection = connection;
        _destination = destination;
        Handle = handle;
        Streams = streams;
        Devices = devices;
        RestoreToken = restoreToken;
    }

    private delegate void BodyWriter(ref MessageWriter writer, string handleToken);

    private delegate void ArgumentWriter(ref MessageWriter writer);

    /// <summary>The session object path.</summary>
    public string Handle { get; }

    public IReadOnlyList<PortalStream> Streams { get; }

    /// <summary>Granted devices (<see cref="DeviceKeyboard"/>, <see cref="DevicePointer"/>).</summary>
    public uint Devices { get; }

    /// <summary>Lets a later session start without the dialog (only when persistence was requested and granted).</summary>
    public string? RestoreToken { get; }

    /// <summary>
    /// Creates the session, asks for keyboard + pointer and all monitors, and starts it (the desktop's dialog). Throws
    /// <see cref="PortalRequestException"/> if the user declines; <see cref="DBusException"/> if there is no portal.
    /// </summary>
    /// <param name="connection">A connected session-bus connection.</param>
    /// <param name="destination">The portal's bus name (<see cref="Service"/>; tests use a fake).</param>
    /// <param name="restoreToken">Token of an earlier session, or <c>null</c>.</param>
    /// <param name="persist">Ask the desktop to remember the permission (installed agent; never for ad-hoc sessions).</param>
    /// <param name="cancellationToken">Cancels waiting for the user.</param>
    public static async Task<PortalSession> StartAsync(Connection connection, string destination, string? restoreToken, bool persist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var created = await RequestAsync(connection, destination, RemoteDesktopInterface, "CreateSession", null, string.Empty,
            (ref MessageWriter w, string token) =>
            {
                var options = w.WriteDictionaryStart();
                Entry(ref w, "handle_token", token);
                Entry(ref w, "session_handle_token", NewToken());
                w.WriteDictionaryEnd(options);
            }, cancellationToken).ConfigureAwait(false);
        var session = created.TryGetValue("session_handle", out var value) ? AsString(value) : null;
        if (string.IsNullOrEmpty(session))
        {
            throw new PortalRequestException("CreateSession", 2);
        }

        try
        {
            await RequestAsync(connection, destination, RemoteDesktopInterface, "SelectDevices", session, "o",
                (ref MessageWriter w, string token) =>
                {
                    w.WriteObjectPath(session);
                    var options = w.WriteDictionaryStart();
                    Entry(ref w, "handle_token", token);
                    Entry(ref w, "types", DeviceKeyboard | DevicePointer);
                    if (persist)
                    {
                        Entry(ref w, "persist_mode", PersistUntilRevoked);
                    }

                    if (!string.IsNullOrEmpty(restoreToken))
                    {
                        Entry(ref w, "restore_token", restoreToken);
                    }

                    w.WriteDictionaryEnd(options);
                }, cancellationToken).ConfigureAwait(false);

            await RequestAsync(connection, destination, ScreenCastInterface, "SelectSources", session, "o",
                (ref MessageWriter w, string token) =>
                {
                    w.WriteObjectPath(session);
                    var options = w.WriteDictionaryStart();
                    Entry(ref w, "handle_token", token);
                    Entry(ref w, "types", SourceMonitor);
                    Entry(ref w, "multiple", true);
                    Entry(ref w, "cursor_mode", CursorEmbedded);
                    w.WriteDictionaryEnd(options);
                }, cancellationToken).ConfigureAwait(false);

            var started = await RequestAsync(connection, destination, RemoteDesktopInterface, "Start", session, "os",
                (ref MessageWriter w, string token) =>
                {
                    w.WriteObjectPath(session);
                    w.WriteString(string.Empty); // no parent window
                    var options = w.WriteDictionaryStart();
                    Entry(ref w, "handle_token", token);
                    w.WriteDictionaryEnd(options);
                }, cancellationToken).ConfigureAwait(false);

            var streams = started.TryGetValue("streams", out var list) ? ParseStreams(list) : [];
            var devices = started.TryGetValue("devices", out var granted) && Unwrap(granted) is { Type: VariantValueType.UInt32 } d ? d.GetUInt32() : 0;
            var token = started.TryGetValue("restore_token", out var restore) ? AsString(restore) : null;
            if (streams.Count == 0)
            {
                throw new PortalRequestException("Start (no screen shared)", 2);
            }

            return new PortalSession(connection, destination, session, streams, devices, token);
        }
        catch
        {
            Close(connection, destination, session);
            throw;
        }
    }

    /// <summary>A PipeWire connection restricted to this session's streams.</summary>
    public Task<SafeFileHandle> OpenPipeWireRemoteAsync()
    {
        MessageBuffer message;
        using (var writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(_destination, ObjectPath, ScreenCastInterface, "OpenPipeWireRemote", "oa{sv}");
            writer.WriteObjectPath(Handle);
            writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
            message = writer.CreateMessage();
        }

        return _connection.CallMethodAsync(message, (Message reply, object? _) =>
            reply.GetBodyReader().ReadHandle<SafeFileHandle>() ?? throw new InvalidDataException("The portal returned no PipeWire file descriptor."));
    }

    // ------------------------------------------------------------------ input (fire and forget, in order)

    /// <summary>Absolute pointer position in the stream's logical coordinates.</summary>
    public void PointerMotionAbsolute(uint stream, double x, double y) => Notify("NotifyPointerMotionAbsolute", "oa{sv}udd", (ref MessageWriter w) =>
    {
        w.WriteUInt32(stream);
        w.WriteDouble(x);
        w.WriteDouble(y);
    });

    /// <summary>Linux evdev button code (<c>BTN_LEFT</c> = 0x110 …).</summary>
    public void PointerButton(int evdevButton, bool pressed) => Notify("NotifyPointerButton", "oa{sv}iu", (ref MessageWriter w) =>
    {
        w.WriteInt32(evdevButton);
        w.WriteUInt32(pressed ? 1u : 0u);
    });

    /// <summary>Wheel notches; axis 0 = vertical (positive = down), 1 = horizontal (positive = right).</summary>
    public void PointerAxisDiscrete(uint axis, int steps) => Notify("NotifyPointerAxisDiscrete", "oa{sv}ui", (ref MessageWriter w) =>
    {
        w.WriteUInt32(axis);
        w.WriteInt32(steps);
    });

    /// <summary>Linux evdev key code.</summary>
    public void KeyboardKeycode(int evdevKey, bool pressed) => Notify("NotifyKeyboardKeycode", "oa{sv}iu", (ref MessageWriter w) =>
    {
        w.WriteInt32(evdevKey);
        w.WriteUInt32(pressed ? 1u : 0u);
    });

    /// <summary>X keysym (text input independent of the keyboard layout).</summary>
    public void KeyboardKeysym(int keysym, bool pressed) => Notify("NotifyKeyboardKeysym", "oa{sv}iu", (ref MessageWriter w) =>
    {
        w.WriteInt32(keysym);
        w.WriteUInt32(pressed ? 1u : 0u);
    });

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Close(_connection, _destination, Handle);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The request object path the portal will use: <c>…/request/&lt;sender&gt;/&lt;token&gt;</c> (internal for tests).</summary>
    internal static string RequestPath(string uniqueName, string token) =>
        $"{ObjectPath}/request/{uniqueName.TrimStart(':').Replace('.', '_')}/{token}";

    /// <summary>Parses <c>a(ua{sv})</c>: node id with <c>position</c> and <c>size</c> (internal for tests).</summary>
    internal static IReadOnlyList<PortalStream> ParseStreams(VariantValue value)
    {
        var streams = new List<PortalStream>();
        value = Unwrap(value);
        if (value.Type != VariantValueType.Array)
        {
            return streams;
        }

        for (var i = 0; i < value.Count; i++)
        {
            var item = Unwrap(value.GetItem(i));
            if (item.Type != VariantValueType.Struct || item.Count < 2)
            {
                continue;
            }

            if (Unwrap(item.GetItem(0)) is not { Type: VariantValueType.UInt32 } nodeValue
                || Unwrap(item.GetItem(1)) is not { Type: VariantValueType.Dictionary } dictionary)
            {
                continue;
            }

            var node = nodeValue.GetUInt32();
            var properties = dictionary.GetDictionary<string, VariantValue>();
            var (x, y) = Pair(properties.GetValueOrDefault("position"));
            var (width, height) = Pair(properties.GetValueOrDefault("size"));
            if (width > 0 && height > 0)
            {
                streams.Add(new PortalStream(node, x, y, width, height));
            }
        }

        return streams;
    }

    private static (int A, int B) Pair(VariantValue value)
    {
        value = Unwrap(value);
        return value.Type == VariantValueType.Struct && value.Count == 2
            && Unwrap(value.GetItem(0)) is { Type: VariantValueType.Int32 } a && Unwrap(value.GetItem(1)) is { Type: VariantValueType.Int32 } b
                ? (a.GetInt32(), b.GetInt32())
                : (0, 0);
    }

    /// <summary>Values nested in variants (e.g. the a{sv} inside <c>streams</c>) report the type <c>Variant</c> until unwrapped.</summary>
    private static VariantValue Unwrap(VariantValue value)
    {
        for (var depth = 0; value.Type == VariantValueType.Variant && depth < 8; depth++)
        {
            value = value.GetVariantValue();
        }

        return value;
    }

    private static string? AsString(VariantValue value) => Unwrap(value) switch
    {
        { Type: VariantValueType.String } s => s.GetString(),
        { Type: VariantValueType.ObjectPath } o => o.GetObjectPathAsString(),
        _ => null,
    };

    private static async Task<Dictionary<string, VariantValue>> RequestAsync(
        Connection connection, string destination, string @interface, string method, string? session, string signaturePrefix, BodyWriter body,
        CancellationToken cancellationToken)
    {
        var token = NewToken();
        var path = RequestPath(connection.UniqueName ?? throw new InvalidOperationException("Not connected."), token);
        var response = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var match = await connection.AddMatchAsync<(uint Code, Dictionary<string, VariantValue> Results)>(
            new MatchRule { Type = MessageType.Signal, Interface = RequestInterface, Member = "Response", Path = path },
            (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                return (code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (Exception? error, (uint Code, Dictionary<string, VariantValue> Results) value, object? _, object? _) =>
            {
                if (error is not null)
                {
                    response.TrySetException(error);
                }
                else
                {
                    response.TrySetResult(value);
                }
            },
            ObserverFlags.None,
            emitOnCapturedContext: false).ConfigureAwait(false);

        MessageBuffer CreateMessage()
        {
            var writer = connection.GetMessageWriter(); // not "using": it is passed by reference
            try
            {
                writer.WriteMethodCallHeader(destination, ObjectPath, @interface, method, signaturePrefix + "a{sv}");
                body(ref writer, token);
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        var handle = await connection.CallMethodAsync(CreateMessage(), (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString())
            .ConfigureAwait(false);
        if (handle != path)
        {
            // Portals before 0.9 choose their own path; not supported (all current desktops use the predictable one).
            throw new PortalRequestException(method + " (unexpected request path)", 2);
        }

        var (code, results) = await response.Task.WaitAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false);
        return code == 0 ? results : throw new PortalRequestException(method, code);
    }

    private void Notify(string method, string signature, ArgumentWriter arguments)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        var writer = _connection.GetMessageWriter(); // not "using": it is passed by reference
        try
        {
            writer.WriteMethodCallHeader(_destination, ObjectPath, RemoteDesktopInterface, method, signature, MessageFlags.NoReplyExpected);
            writer.WriteObjectPath(Handle);
            writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
            arguments(ref writer);
            _ = _connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void Close(Connection connection, string destination, string session)
    {
        try
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination, session, SessionInterface, "Close", null, MessageFlags.NoReplyExpected);
            _ = connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex) when (ex is DisconnectedException or ObjectDisposedException)
        {
            // the bus is gone – so is the session
        }
    }

    private static void Entry(ref MessageWriter writer, string key, VariantValue value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariant(value);
    }

    private static string NewToken() => "mkits" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
