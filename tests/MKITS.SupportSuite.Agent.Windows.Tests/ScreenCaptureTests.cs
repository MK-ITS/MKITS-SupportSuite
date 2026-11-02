// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Agent.Windows.Platform.Capture;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

public sealed class ScreenCaptureTests
{
    private static readonly DesktopDisplay Monitor1 = new(new MonitorDescriptor(1, "1", 0, 0, 64, 48, true), @"\\.\DISPLAY1");

    [Fact]
    public void Capture_FirstGrabberFails_NextOneDeliversTheImage()
    {
        var dxgi = new FakeGrabber(succeed: false);
        var gdi = new FakeGrabber(succeed: true);
        using var source = new DesktopScreenSource([dxgi, gdi], () => [Monitor1]);

        var frame = source.Capture(1, 60, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(VideoCodec.Jpeg, frame.Codec);
        Assert.Equal((64, 48), (frame.Width, frame.Height));
        Assert.Equal([0xFF, 0xD8], frame.Data.Take(2)); // JPEG start of image
        Assert.Equal(1, dxgi.Calls);
        Assert.Equal(1, gdi.Calls);
    }

    [Fact]
    public void Capture_NoGrabberOrUnknownMonitor_ReturnsNull()
    {
        var grabber = new FakeGrabber(succeed: false);
        using var source = new DesktopScreenSource([grabber], () => [Monitor1]);

        Assert.Null(source.Capture(1, 60, CancellationToken.None));
        Assert.Null(source.Capture(7, 60, CancellationToken.None));
        Assert.Equal(1, grabber.Calls); // the unknown monitor never reaches a grabber
    }

    [Fact]
    public void Capture_MonitorResized_FrameIsReallocatedAndStartsEmpty()
    {
        var grabber = new FakeGrabber(succeed: true);
        var display = Monitor1;
        using var source = new DesktopScreenSource([grabber], () => [display]);

        source.Capture(1, 60, CancellationToken.None);
        source.Capture(1, 60, CancellationToken.None);
        display = Monitor1 with { Monitor = Monitor1.Monitor with { Width = 32, Height = 24 } };
        var resized = source.Capture(1, 60, CancellationToken.None);

        Assert.Equal([false, true, false], grabber.HadContent);
        Assert.Equal((32, 24), (resized!.Width, resized.Height));
    }

    [Fact]
    public void Dispose_DisposesGrabbers_LaterCapturesReturnNull()
    {
        var grabber = new FakeGrabber(succeed: true);
        var source = new DesktopScreenSource([grabber], () => [Monitor1]);

        source.Dispose();
        source.Dispose();

        Assert.True(grabber.Disposed);
        Assert.Null(source.Capture(1, 60, CancellationToken.None));
    }

    [Fact]
    public void FrameBuffer_BitmapIsAViewOfThePixels()
    {
        using var frame = new FrameBuffer();
        frame.EnsureSize(4, 2);
        frame.Pixels[(1 * frame.Stride) + 4] = 0x11; // pixel (1, 1): blue
        frame.Pixels[(1 * frame.Stride) + 5] = 0x22; // green
        frame.Pixels[(1 * frame.Stride) + 6] = 0x33; // red

        Assert.Equal(System.Drawing.Color.FromArgb(0x33, 0x22, 0x11).ToArgb(), frame.Bitmap.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void DxgiGrabber_UnknownOutput_IsLeftToGdi()
    {
        var time = new ManualTime();
        using var dxgi = new DxgiGrabber(time);
        using var frame = new FrameBuffer();
        frame.EnsureSize(64, 48);
        var nowhere = Monitor1 with { DeviceName = @"\\.\NO-SUCH-DISPLAY" };

        // Enumerates the real adapters and outputs (exercises the DXGI calls) without finding the name.
        Assert.False(dxgi.Grab(nowhere, frame));
        time.Advance(DxgiGrabber.RetryAfter / 2);
        Assert.False(dxgi.Grab(nowhere, frame));
        time.Advance(DxgiGrabber.RetryAfter);
        Assert.False(dxgi.Grab(nowhere, frame));
        Assert.False(frame.HasContent);
    }

    [Fact]
    public void RealScreen_EveryMonitorCanBeCaptured_WithDxgiOrGdi()
    {
        using var source = new DesktopScreenSource();
        foreach (var monitor in source.GetMonitors())
        {
            // Twice: DXGI's second frame usually times out (unchanged screen) and must reuse the first image.
            var first = source.Capture(monitor.Id, 60, CancellationToken.None);
            var second = source.Capture(monitor.Id, 60, CancellationToken.None);
            if (first is null)
            {
                continue; // no interactive desktop on this machine (service session): nothing can capture
            }

            Assert.Equal((monitor.Width, monitor.Height), (first.Width, first.Height));
            Assert.NotNull(second);
        }
    }

    private sealed class FakeGrabber(bool succeed) : IDesktopGrabber
    {
        public int Calls { get; private set; }

        public List<bool> HadContent { get; } = [];

        public bool Disposed { get; private set; }

        public bool Grab(DesktopDisplay display, FrameBuffer frame)
        {
            Calls++;
            HadContent.Add(frame.HasContent);
            if (succeed)
            {
                frame.Pixels.Fill(0x80);
                frame.HasContent = true;
            }

            return succeed;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
