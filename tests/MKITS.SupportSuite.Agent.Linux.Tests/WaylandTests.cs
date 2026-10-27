// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Wayland;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

/// <summary>Wayland building blocks: SPA format PODs, PipeWire capture (private daemon) and portal input mapping.</summary>
public sealed class WaylandTests(PipeWireFixture pipewire) : IClassFixture<PipeWireFixture>
{
    // Produced by libspa's spa_pod_builder_add_object with the same values (PipeWire 1.0).
    private const string ExpectedEnumFormat =
        "e00000000f000000030004000300000001000000000000000400000003000000020000000000000002000000000000000400000003000000010000000000000001000200000000002400000013000000030000000000000004000000030000000800000008000000070000000c0000000b00000000000000030002000000000028000000130000000100000000000000080000000a000000800700003804000001000000010000000020000000200000040002000000000028000000130000000100000000000000080000000b0000001e000000010000000000000001000000e803000001000000";

    // A negotiated format from libspa: RGBx, 1280x720, variable frame rate.
    private const string NegotiatedFormat =
        "800000000f00000003000400040000000100000000000000040000000300000002000000000000000200000000000000040000000300000001000000000000000100020000000000040000000300000007000000000000000300020000000000080000000a00000000050000d00200000400020000000000080000000b0000000000000001000000";

    private static readonly PortalStream[] TwoScreens = [new(41, 0, 0, 1920, 1080), new(42, 1920, 0, 1280, 1024)];

    private static readonly string[] ExpectedInput =
    [
        "motion 42 80 100",
        "button 272 True",
        "button 273 False",
        "axis 0 -2",
        "key 30 True",
        "keysym 233 True",
        "keysym 233 False",
    ];

    [Fact]
    public void SpaPod_EnumFormat_MatchesLibspa() =>
        Assert.Equal(ExpectedEnumFormat, Convert.ToHexStringLower(SpaPod.EnumFormat()));

    [Fact]
    public void SpaPod_ParseFormat_ReadsFormatAndSize_RejectsGarbage()
    {
        var bytes = Convert.FromHexString(NegotiatedFormat);
        var wrongType = (byte[])bytes.Clone();
        wrongType[4] = 0x0e; // Struct instead of Object
        var tooLarge = (byte[])bytes.Clone();
        tooLarge[0] = 0xF0; // claims more than there is

        var format = SpaPod.ParseFormat(bytes);

        Assert.Equal(new VideoFormat(SpaPod.VideoFormatRgbx, 1280, 720), format);
        Assert.False(format!.Bgrx);
        Assert.True(format.IsSupported);
        Assert.Null(SpaPod.ParseFormat(bytes.AsSpan(0, 60)));
        Assert.Null(SpaPod.ParseFormat(wrongType));
        Assert.Null(SpaPod.ParseFormat(tooLarge));
        Assert.Null(SpaPod.ParseFormat([]));

        // The offered formats parse to their defaults (choices: first value).
        Assert.Equal(new VideoFormat(SpaPod.VideoFormatBgrx, 1920, 1080), SpaPod.ParseFormat(SpaPod.EnumFormat()));
    }

    [Fact]
    public void PipeWireCapture_ReceivesFramesOfAStream()
    {
        Assert.True(pipewire.Available, "pipewire, wireplumber, gst-launch-1.0 (gstreamer1.0-pipewire) and dbus-daemon are needed");

        using var capture = PipeWireCapture.Connect(null, 0, PipeWireFixture.SourceNode, pipewire.Socket);

        Assert.NotNull(capture);
        Assert.True(capture.WaitForFrame(TimeSpan.FromSeconds(30)), "no frame from the test source");
        Assert.Equal(new VideoFormat(SpaPod.VideoFormatBgrx, 320, 240), capture.Format);

        // The very first buffer of a fresh consumer can be blank with GStreamer's sink; the test pattern follows.
        var pixel = -1;
        for (var i = 0; i < 50 && pixel != 0xFF0000; i++)
        {
            Thread.Sleep(100);
            pixel = capture.WithLatestFrame(
                (pixels, width, height, stride, bgrx) =>
                    bgrx && stride >= width * 4 ? Marshal.ReadInt32(pixels + (stride * (height / 2)) + (width / 2 * 4)) & 0xFFFFFF : -2,
                -1);
        }

        Assert.Equal(0xFF0000, pixel); // red in BGRx: B=0, G=0, R=255
        Assert.True(capture.Frames > 1);
        Assert.False(capture.Failed);
    }

    [Fact]
    public void PortalInput_MapsDesktopCoordinatesToTheStream_AndButtonsToEvdev()
    {
        var portal = new RecordingPortal(TwoScreens);
        var input = new PortalInputInjector(portal);

        input.MovePointer(2000, 100);  // second screen
        input.MovePointer(5000, 5000); // outside every screen: dropped
        input.SetButton(PointerButtons.Left, down: true);
        input.SetButton(PointerButtons.Right, down: false);
        input.Wheel(0, 2);             // up two notches
        input.Key("KeyA", down: true);
        input.Text("é");

        Assert.Equal(ExpectedInput, portal.Calls);
    }

    [Fact]
    public void PortalSession_RequestPath_FollowsThePortalConvention() =>
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/mkits1", PortalSession.RequestPath(":1.42", "mkits1"));

    private sealed class RecordingPortal(IReadOnlyList<PortalStream> streams) : IPortalInput
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<PortalStream> Streams => streams;

        public void PointerMotionAbsolute(uint stream, double x, double y) => Calls.Add(FormattableString.Invariant($"motion {stream} {x} {y}"));

        public void PointerButton(int evdevButton, bool pressed) => Calls.Add(FormattableString.Invariant($"button {evdevButton} {pressed}"));

        public void PointerAxisDiscrete(uint axis, int steps) => Calls.Add(FormattableString.Invariant($"axis {axis} {steps}"));

        public void KeyboardKeycode(int evdevKey, bool pressed) => Calls.Add(FormattableString.Invariant($"key {evdevKey} {pressed}"));

        public void KeyboardKeysym(int keysym, bool pressed) => Calls.Add(FormattableString.Invariant($"keysym {keysym} {pressed}"));
    }
}
