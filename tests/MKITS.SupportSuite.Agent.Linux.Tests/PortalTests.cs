// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using MKITS.SupportSuite.Agent.Linux.Wayland;
using Tmds.DBus.Protocol;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

/// <summary>
/// <see cref="PortalSession"/> against a fake xdg-desktop-portal on a private session bus: the request/response
/// handshake, the options sent, the parsed streams, the PipeWire descriptor and the input calls.
/// </summary>
public sealed class PortalTests(PipeWireFixture bus) : IClassFixture<PipeWireFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] ExpectedInput = ["NotifyPointerMotionAbsolute 42 10.5 20", "NotifyPointerButton 272 1", "NotifyKeyboardKeycode 30 0"];

    private Connection? _portalConnection;
    private Connection? _client;
    private FakePortal? _portal;

    public async Task InitializeAsync()
    {
        Assert.True(bus.Available, "dbus-daemon is needed");
        _portalConnection = new Connection(bus.BusAddress);
        await _portalConnection.ConnectAsync();
        _portal = new FakePortal(_portalConnection);
        _portalConnection.AddMethodHandler(_portal);
        _client = new Connection(bus.BusAddress);
        await _client.ConnectAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _portalConnection?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Start_RunsTheHandshake_AndParsesStreams()
    {
        await using var session = await PortalSession.StartAsync(_client!, _portalConnection!.UniqueName!, "old-token", persist: true, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.Equal([new PortalStream(41, 0, 0, 1920, 1080), new PortalStream(42, 1920, 0, 1280, 1024)], session.Streams);
        Assert.Equal(PortalSession.DeviceKeyboard | PortalSession.DevicePointer, session.Devices);
        Assert.Equal("new-token", session.RestoreToken);
        Assert.Equal(FakePortal.SessionHandle, session.Handle);
        Assert.Equal("types=3 persist_mode=2 restore_token=old-token", _portal!.SelectDevices);
        Assert.Equal("types=1 multiple=True cursor_mode=2", _portal.SelectSources);

        using var remote = await session.OpenPipeWireRemoteAsync().WaitAsync(Timeout);
        Assert.False(remote.IsInvalid);

        session.PointerMotionAbsolute(42, 10.5, 20);
        session.PointerButton(PortalInputInjector.ButtonLeft, pressed: true);
        session.KeyboardKeycode(30, pressed: false);
        await Eventually(() => _portal.Input.Count >= 3);
        Assert.Equal(ExpectedInput, _portal.Input);
    }

    [Fact]
    public async Task Start_UserDeclines_Throws()
    {
        _portal!.StartResponse = 1;

        var error = await Assert.ThrowsAsync<PortalRequestException>(() =>
            PortalSession.StartAsync(_client!, _portalConnection!.UniqueName!, null, persist: false, CancellationToken.None).WaitAsync(Timeout));

        Assert.True(error.Cancelled);
        Assert.Equal("types=3", _portal.SelectDevices); // no persistence for ad-hoc sessions
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    private delegate void ResultsWriter(ref MessageWriter writer);

    /// <summary>Answers like xdg-desktop-portal: a request object path, then the <c>Response</c> signal on it.</summary>
    private sealed class FakePortal(Connection connection) : IMethodHandler
    {
        public const string SessionHandle = "/org/freedesktop/portal/desktop/session/1_1/mkits";

        public string Path => PortalSession.ObjectPath;

        public uint StartResponse { get; set; }

        public string? SelectDevices { get; private set; }

        public string? SelectSources { get; private set; }

        public ConcurrentQueue<string> Input { get; } = new();

        public bool RunMethodHandlerSynchronously(Message message) => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            var sender = request.SenderAsString!;
            var reader = request.GetBodyReader();
            switch (request.MemberAsString)
            {
                case "CreateSession":
                {
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    Respond(context, sender, options["handle_token"].GetString(), 0, (ref MessageWriter w) => Entry(ref w, "session_handle", SessionHandle));
                    break;
                }

                case "SelectDevices":
                {
                    _ = reader.ReadObjectPathAsString();
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    SelectDevices = $"types={options["types"].GetUInt32()}"
                        + (options.TryGetValue("persist_mode", out var persist) ? $" persist_mode={persist.GetUInt32()}" : string.Empty)
                        + (options.TryGetValue("restore_token", out var token) ? $" restore_token={token.GetString()}" : string.Empty);
                    Respond(context, sender, options["handle_token"].GetString(), 0, (ref MessageWriter _) => { });
                    break;
                }

                case "SelectSources":
                {
                    _ = reader.ReadObjectPathAsString();
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    SelectSources = $"types={options["types"].GetUInt32()} multiple={options["multiple"].GetBool()} cursor_mode={options["cursor_mode"].GetUInt32()}";
                    Respond(context, sender, options["handle_token"].GetString(), 0, (ref MessageWriter _) => { });
                    break;
                }

                case "Start":
                {
                    _ = reader.ReadObjectPathAsString();
                    _ = reader.ReadString();
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    Respond(context, sender, options["handle_token"].GetString(), StartResponse, WriteStartResults);
                    break;
                }

                case "OpenPipeWireRemote":
                {
                    using var file = File.OpenHandle("/dev/null");
                    using var writer = context.CreateReplyWriter("h");
                    writer.WriteHandle(file);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "NotifyPointerMotionAbsolute":
                    _ = reader.ReadObjectPathAsString();
                    _ = reader.ReadDictionaryOfStringToVariantValue();
                    Input.Enqueue(FormattableString.Invariant($"NotifyPointerMotionAbsolute {reader.ReadUInt32()} {reader.ReadDouble()} {reader.ReadDouble()}"));
                    break;

                case "NotifyPointerButton" or "NotifyKeyboardKeycode":
                    _ = reader.ReadObjectPathAsString();
                    _ = reader.ReadDictionaryOfStringToVariantValue();
                    Input.Enqueue(FormattableString.Invariant($"{request.MemberAsString} {reader.ReadInt32()} {reader.ReadUInt32()}"));
                    break;

                default:
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "not faked");
                    break;
            }

            return ValueTask.CompletedTask;
        }

        private static void WriteStartResults(ref MessageWriter w)
        {
            Entry(ref w, "devices", PortalSession.DeviceKeyboard | PortalSession.DevicePointer);
            Entry(ref w, "restore_token", "new-token");
            w.WriteDictionaryEntryStart();
            w.WriteString("streams");
            w.WriteSignature("a(ua{sv})");
            var streams = w.WriteArrayStart(DBusType.Struct);
            Stream(ref w, 41, 0, 0, 1920, 1080);
            Stream(ref w, 42, 1920, 0, 1280, 1024);
            w.WriteArrayEnd(streams);
        }

        private static void Stream(ref MessageWriter w, uint node, int x, int y, int width, int height)
        {
            w.WriteStructureStart();
            w.WriteUInt32(node);
            var properties = w.WriteDictionaryStart();
            Entry(ref w, "position", VariantValue.Struct(x, y));
            Entry(ref w, "size", VariantValue.Struct(width, height));
            Entry(ref w, "source_type", 1u);
            w.WriteDictionaryEnd(properties);
        }

        private static void Entry(ref MessageWriter w, string key, VariantValue value)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(key);
            w.WriteVariant(value);
        }

        private void Respond(MethodContext context, string sender, string token, uint response, ResultsWriter results)
        {
            var path = PortalSession.RequestPath(sender, token);
            using (var reply = context.CreateReplyWriter("o"))
            {
                reply.WriteObjectPath(path);
                context.Reply(reply.CreateMessage());
            }

            var signal = connection.GetMessageWriter(); // not "using": passed by reference
            try
            {
                signal.WriteSignalHeader(sender, path, PortalSession.RequestInterface, "Response", "ua{sv}");
                signal.WriteUInt32(response);
                var dictionary = signal.WriteDictionaryStart();
                results(ref signal);
                signal.WriteDictionaryEnd(dictionary);
                _ = connection.TrySendMessage(signal.CreateMessage());
            }
            finally
            {
                signal.Dispose();
            }
        }
    }
}
