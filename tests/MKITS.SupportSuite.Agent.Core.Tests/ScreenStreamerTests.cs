// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class ScreenStreamerTests : IAsyncDisposable
{
    private readonly FakeScreenSource _screen = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingContext _context = new();
    private readonly ScreenStreamer _streamer;

    public ScreenStreamerTests() => _streamer = new ScreenStreamer(_screen, _time);

    public ValueTask DisposeAsync() => _streamer.DisposeAsync();

    [Fact]
    public async Task ViewerReady_SendsMonitorList()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);

        var (viewerId, payload) = Assert.Single(_context.Sent);
        Assert.Equal("v1", viewerId);
        Assert.Equal(2, payload.MonitorList.Monitors.Count);
        Assert.True(payload.MonitorList.Monitors[0].Primary);
    }

    [Fact]
    public async Task Tick_SendsPrimaryMonitor_WithIncreasingSequence()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);

        await TickAsync();
        _screen.Content++;
        await TickAsync();

        var frames = _context.Frames("v1");
        Assert.Equal([1UL, 2UL], frames.Select(f => f.FrameSequence));
        Assert.All(frames, f => Assert.Equal(1u, f.MonitorId));
        Assert.All(frames, f => Assert.Equal(VideoCodec.Jpeg, f.Codec));
    }

    [Fact]
    public async Task FlowControl_StopsAfterThreeUnacknowledged_AndResumesAfterAck()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            _screen.Content++;
            await TickAsync();
        }

        Assert.Equal(ScreenStreamer.MaxFramesInFlight, _context.Frames("v1").Count);

        await _streamer.HandleAsync(_context, "v1", new SessionPayload { FrameAck = new FrameAck { FrameSequence = 2 } }, CancellationToken.None);
        for (var i = 0; i < 4; i++)
        {
            _screen.Content++;
            await TickAsync();
        }

        Assert.Equal(5, _context.Frames("v1").Count);
    }

    [Fact]
    public async Task UnchangedImage_IsNotResent_UntilRefreshInterval()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        await TickAsync();
        await Ack("v1");

        await TickAsync(TimeSpan.FromMilliseconds(500));
        await TickAsync(TimeSpan.FromMilliseconds(500));
        Assert.Single(_context.Frames("v1"));

        _time.Advance(TimeSpan.FromSeconds(1));
        await TickAsync();
        Assert.Equal(2, _context.Frames("v1").Count);
    }

    [Fact]
    public async Task SelectMonitor_SwitchesMonitor_UnknownIsIgnored()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        await _streamer.HandleAsync(_context, "v1", new SessionPayload { SelectMonitor = new SelectMonitor { MonitorId = 99 } }, CancellationToken.None);
        await TickAsync();
        await Ack("v1");

        await _streamer.HandleAsync(_context, "v1", new SessionPayload { SelectMonitor = new SelectMonitor { MonitorId = 2 } }, CancellationToken.None);
        await TickAsync();

        Assert.Equal([1u, 2u], _context.Frames("v1").Select(f => f.MonitorId));
        Assert.Equal(1280u, _context.Frames("v1")[1].Width);
    }

    [Fact]
    public async Task QualitySettings_LimitFrameRate()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        await _streamer.HandleAsync(_context, "v1", new SessionPayload { QualitySettings = new QualitySettings { MaxFps = 2 } }, CancellationToken.None);

        await TickAsync();
        await Ack("v1");
        _screen.Content++;
        await TickAsync(TimeSpan.FromMilliseconds(100)); // too early for 2 fps
        Assert.Single(_context.Frames("v1"));

        await TickAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, _context.Frames("v1").Count);
    }

    [Fact]
    public async Task ViewerGone_StopsStreamingToIt()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        await _streamer.OnViewerReadyAsync(_context, "v2", CancellationToken.None);
        _streamer.OnViewerGone("v1");

        await TickAsync();

        Assert.Empty(_context.Frames("v1"));
        Assert.Single(_context.Frames("v2"));
    }

    [Fact]
    public async Task TwoViewersOnSameMonitor_ShareOneCapture()
    {
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        await _streamer.OnViewerReadyAsync(_context, "v2", CancellationToken.None);

        await TickAsync();

        Assert.Equal(1, _screen.Captures);
    }

    [Fact]
    public async Task CaptureUnavailable_SendsNothing()
    {
        _screen.Unavailable = true;
        await _streamer.OnViewerReadyAsync(_context, "v1", CancellationToken.None);

        await TickAsync();

        Assert.Empty(_context.Frames("v1"));
    }

    private async Task TickAsync(TimeSpan? advance = null)
    {
        _time.Advance(advance ?? TimeSpan.FromSeconds(1));
        await _streamer.TickAsync(_context, CancellationToken.None);
    }

    private Task Ack(string viewerId)
    {
        var last = _context.Frames(viewerId)[^1].FrameSequence;
        return _streamer.HandleAsync(_context, viewerId, new SessionPayload { FrameAck = new FrameAck { FrameSequence = last } }, CancellationToken.None);
    }
}
