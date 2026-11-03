// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.IO.Pipes;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Elevation;
using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

/// <summary>H.264 through the privileged helper: the pipe protocol end to end, its limits, and switching sides.</summary>
public sealed class HelperStreamingTests
{
    [Fact]
    public async Task HelperClient_StreamsThroughThePipe()
    {
        var screen = new FakeSource(VideoCodec.H264);
        await using var helper = await LoopbackHelper.StartAsync(screen);

        Assert.Equal([VideoCodec.H264], helper.Client.StreamCodecs);
        var first = helper.Client.CaptureStream(1, "viewer-1", VideoCodec.H264, 60, keyframe: true, CancellationToken.None);
        var second = helper.Client.CaptureStream(1, "viewer-1", VideoCodec.H264, 60, keyframe: false, CancellationToken.None);
        var jpeg = helper.Client.Capture(1, 60, CancellationToken.None);
        helper.Client.ReleaseStream("viewer-1");
        _ = helper.Client.GetMonitors(); // a request with a response: the release before it has been handled

        Assert.Equal((VideoCodec.H264, true), (first!.Codec, first.Keyframe));
        Assert.Equal((VideoCodec.H264, false), (second!.Codec, second.Keyframe));
        Assert.Equal([1, 2, 3], second.Data);
        Assert.Equal((VideoCodec.Jpeg, true), (jpeg!.Codec, jpeg.Keyframe));
        Assert.Equal(["viewer-1"], screen.Released);
    }

    [Fact]
    public void Handler_RefusesUnsupportedCodecsTooManyStreamsAndBadKeys()
    {
        var screen = new FakeSource(VideoCodec.H264);
        var handler = new HelperRequestHandler(screen, new FakeInput(), () => { });

        Assert.False(HasFrame(handler.Handle(StreamRequest("v", VideoCodec.Jpeg))));
        for (var i = 0; i < HelperRequestHandler.MaxStreams; i++)
        {
            Assert.True(HasFrame(handler.Handle(StreamRequest($"v{i}", VideoCodec.H264))));
        }

        Assert.False(HasFrame(handler.Handle(StreamRequest("one-too-many", VideoCodec.H264))));
        Assert.True(HasFrame(handler.Handle(StreamRequest("v0", VideoCodec.H264)))); // existing streams go on
        Assert.Throws<InvalidDataException>(() => handler.Handle(StreamRequest(new string('x', HelperRequestHandler.MaxStreamKeyLength + 1), VideoCodec.H264)));
        Assert.Throws<InvalidDataException>(() => handler.Handle(StreamRequest(string.Empty, VideoCodec.H264)));

        // Releasing frees a slot; unknown keys are ignored (never forwarded).
        Assert.Null(handler.Handle(HelperProtocol.Build(HelperRequest.ReleaseStream, w => w.Write("v3"))));
        Assert.Null(handler.Handle(HelperProtocol.Build(HelperRequest.ReleaseStream, w => w.Write("never-opened"))));
        Assert.True(HasFrame(handler.Handle(StreamRequest("one-too-many", VideoCodec.H264))));
        Assert.Equal(["v3"], screen.Released);
    }

    [Fact]
    public void Switchable_ReleasesTheOldSidesStream_AndForcesAKeyframe()
    {
        var own = new FakeSource(VideoCodec.H264);
        var helper = new FakeSource(VideoCodec.H264);
        var jpegOnly = new FakeSource();
        var switchable = new SwitchableScreenSource(own);

        switchable.CaptureStream(1, "v", VideoCodec.H264, 60, keyframe: true, CancellationToken.None);
        switchable.CaptureStream(1, "v", VideoCodec.H264, 60, keyframe: false, CancellationToken.None);
        switchable.Current = helper;
        switchable.CaptureStream(1, "v", VideoCodec.H264, 60, keyframe: false, CancellationToken.None);
        switchable.Current = jpegOnly;
        var fallback = switchable.CaptureStream(1, "v", VideoCodec.H264, 60, keyframe: false, CancellationToken.None);
        switchable.Current = own;
        switchable.CaptureStream(1, "v", VideoCodec.H264, 60, keyframe: false, CancellationToken.None);
        switchable.ReleaseStream("v");
        switchable.ReleaseStream("v");

        Assert.Equal([true, false, true], own.StreamKeyframes); // back on its own side after the JPEG detour: a fresh start
        Assert.Equal([true], helper.StreamKeyframes);
        Assert.Equal(["v", "v"], own.Released); // when the helper took over, and at the end
        Assert.Equal(["v"], helper.Released); // when the JPEG-only side took over
        Assert.Equal(VideoCodec.Jpeg, fallback!.Codec);
    }

    private static byte[] StreamRequest(string key, VideoCodec codec) => HelperProtocol.Build(HelperRequest.CaptureStream, w =>
    {
        w.Write(1u);
        w.Write(key);
        w.Write((int)codec);
        w.Write(60);
        w.Write(false);
    });

    private static bool HasFrame(byte[]? response) => response is { Length: > 1 } && response[1] != 0;

    /// <summary>A helper on a local pipe, serving with <see cref="HelperRequestHandler"/> like the real one.</summary>
    private sealed class LoopbackHelper : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly Task _serving;

        private LoopbackHelper(NamedPipeServerStream server, HelperClient client, Task serving)
        {
            _server = server;
            Client = client;
            _serving = serving;
        }

        public HelperClient Client { get; }

        public static async Task<LoopbackHelper> StartAsync(IScreenSource screen)
        {
            var name = $"mkits-test-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(), pipe.ConnectAsync());
            var handler = new HelperRequestHandler(screen, new FakeInput(), () => { });
            var serving = Task.Run(async () =>
            {
                while (await HelperProtocol.ReadAsync(server, CancellationToken.None) is { } message)
                {
                    if (handler.Handle(message) is { Length: > 0 } response)
                    {
                        await HelperProtocol.WriteAsync(server, response, CancellationToken.None);
                    }
                }
            });
            return new LoopbackHelper(server, new HelperClient(pipe), serving);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _serving;
            await _server.DisposeAsync();
        }
    }

    private sealed class FakeSource(params VideoCodec[] streams) : IScreenSource
    {
        public List<string> Released { get; } = [];

        public List<bool> StreamKeyframes { get; } = [];

        public IReadOnlyCollection<VideoCodec> StreamCodecs => streams;

        public IReadOnlyList<MonitorDescriptor> GetMonitors() => [new MonitorDescriptor(1, "1", 0, 0, 64, 48, true)];

        public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken) =>
            new(VideoCodec.Jpeg, 64, 48, [0xFF, 0xD8], Keyframe: true);

        public EncodedFrame? CaptureStream(uint monitorId, string streamKey, VideoCodec codec, int quality, bool keyframe, CancellationToken cancellationToken)
        {
            StreamKeyframes.Add(keyframe);
            return new EncodedFrame(codec, 64, 48, [1, 2, 3], keyframe);
        }

        public void ReleaseStream(string streamKey) => Released.Add(streamKey);
    }

    private sealed class FakeInput : IInputInjector
    {
        public void MovePointer(int x, int y)
        {
        }

        public void SetButton(PointerButtons button, bool down)
        {
        }

        public void Wheel(int deltaX, int deltaY)
        {
        }

        public void Key(string code, bool down)
        {
        }

        public void Text(string text)
        {
        }

        public void Special(SpecialKeyKind kind)
        {
        }
    }
}
