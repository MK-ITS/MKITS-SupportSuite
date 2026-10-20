// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;
using WireMonitor = MKITS.SupportSuite.Common.Protocol.V0.Monitor;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>A monitor in virtual-desktop coordinates.</summary>
public sealed record MonitorDescriptor(uint Id, string Name, int Left, int Top, int Width, int Height, bool Primary);

/// <summary>An encoded screen image.</summary>
public sealed record EncodedFrame(VideoCodec Codec, int Width, int Height, byte[] Data, bool Keyframe);

/// <summary>Platform screen capture + encoding (Windows: GDI/DXGI, Linux: X11/PipeWire, Android: MediaProjection).</summary>
public interface IScreenSource
{
    IReadOnlyList<MonitorDescriptor> GetMonitors();

    /// <summary>Captures and encodes one monitor; <paramref name="quality"/> 1–100. <c>null</c> if capturing is not possible right now (e.g. secure desktop).</summary>
    EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken);
}

/// <summary>
/// Streams the screen to every ready viewer with flow control (docs/protocol.md §5): at most
/// <see cref="MaxFramesInFlight"/> unacknowledged frames per viewer, unchanged images are not sent again,
/// frame rate and quality per viewer via <c>QualitySettings</c>, monitor via <c>SelectMonitor</c>.
/// </summary>
public sealed class ScreenStreamer : ISessionChannelHandler, IAsyncDisposable
{
    internal const int MaxFramesInFlight = 3;
    internal const uint DefaultFps = 10;
    internal const uint MaxFps = 30;

    private readonly IScreenSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ViewerStream> _viewers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _loopLock = new();
    private ISessionContext? _context;
    private Task? _loop;

    public ScreenStreamer(IScreenSource source, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var monitors = _source.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.Primary) ?? (monitors.Count > 0 ? monitors[0] : null);
        _viewers[viewerId] = new ViewerStream(primary?.Id ?? 0);

        var list = new MonitorList();
        list.Monitors.AddRange(monitors.Select(m => new WireMonitor
        {
            Id = m.Id,
            Name = m.Name,
            Width = (uint)m.Width,
            Height = (uint)m.Height,
            Primary = m.Primary,
        }));
        await context.SendAsync(viewerId, new SessionPayload { MonitorList = list }, cancellationToken).ConfigureAwait(false);

        lock (_loopLock)
        {
            _context = context;
            _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        }
    }

    public void OnViewerGone(string viewerId) => _viewers.TryRemove(viewerId, out _);

    public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!_viewers.TryGetValue(viewerId, out var viewer))
        {
            return Task.CompletedTask;
        }

        switch (payload.BodyCase)
        {
            case SessionPayload.BodyOneofCase.FrameAck:
                viewer.Acknowledge(payload.FrameAck.FrameSequence);
                break;
            case SessionPayload.BodyOneofCase.SelectMonitor:
                if (_source.GetMonitors().Any(m => m.Id == payload.SelectMonitor.MonitorId))
                {
                    viewer.MonitorId = payload.SelectMonitor.MonitorId;
                    viewer.ForceNextFrame();
                }

                break;
            case SessionPayload.BodyOneofCase.QualitySettings:
                viewer.Fps = Math.Clamp(payload.QualitySettings.MaxFps == 0 ? DefaultFps : payload.QualitySettings.MaxFps, 1, MaxFps);
                viewer.Quality = payload.QualitySettings.PreferQuality ? 85 : 60;
                viewer.ForceNextFrame();
                break;
            default:
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>One streaming step for all viewers; public for tests (the loop calls it on every tick).</summary>
    public async Task TickAsync(ISessionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _timeProvider.GetUtcNow();
        var captured = new Dictionary<(uint Monitor, int Quality), EncodedFrame?>();

        foreach (var (viewerId, viewer) in _viewers)
        {
            if (!viewer.IsDue(now) || viewer.InFlight >= MaxFramesInFlight)
            {
                continue;
            }

            viewer.MarkAttempt(now);

            var key = (viewer.MonitorId, viewer.Quality);
            if (!captured.TryGetValue(key, out var frame))
            {
                frame = _source.Capture(viewer.MonitorId, viewer.Quality, cancellationToken);
                captured[key] = frame;
            }

            if (frame is null)
            {
                continue;
            }

            var hash = SHA256.HashData(frame.Data);
            if (!viewer.ShouldSend(hash, now))
            {
                continue;
            }

            var sequence = viewer.NextSequence();
            await context.SendAsync(viewerId, new SessionPayload
            {
                VideoFrame = new VideoFrame
                {
                    MonitorId = viewer.MonitorId,
                    Codec = frame.Codec,
                    Keyframe = frame.Keyframe,
                    TimestampUs = (ulong)(now.ToUnixTimeMilliseconds() * 1000),
                    Width = (uint)frame.Width,
                    Height = (uint)frame.Height,
                    Data = ByteString.CopyFrom(frame.Data),
                    FrameSequence = sequence,
                },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / MaxFps), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_context is { } context && !_viewers.IsEmpty)
            {
                try
                {
                    await TickAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failing capture (e.g. display mode change) must not stop the stream; try again next tick.
                }
            }
        }
    }

    private sealed class ViewerStream(uint monitorId)
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

        private readonly object _lock = new();
        private ulong _sequence;
        private ulong _acknowledged;
        private byte[]? _lastHash;
        private DateTimeOffset _lastSent;
        private DateTimeOffset _lastAttempt;
        private bool _force = true;

        public uint MonitorId { get; set; } = monitorId;

        public uint Fps { get; set; } = DefaultFps;

        public int Quality { get; set; } = 60;

        public int InFlight
        {
            get
            {
                lock (_lock)
                {
                    return (int)(_sequence - _acknowledged);
                }
            }
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (_lock)
            {
                return now - _lastAttempt >= TimeSpan.FromSeconds(1.0 / Fps);
            }
        }

        public void MarkAttempt(DateTimeOffset now)
        {
            lock (_lock)
            {
                _lastAttempt = now;
            }
        }

        /// <summary>Unchanged images are skipped, but re-sent every few seconds (late joiners, lost frames).</summary>
        public bool ShouldSend(byte[] hash, DateTimeOffset now)
        {
            lock (_lock)
            {
                if (!_force && _lastHash is not null && hash.AsSpan().SequenceEqual(_lastHash) && now - _lastSent < RefreshInterval)
                {
                    return false;
                }

                _force = false;
                _lastHash = hash;
                _lastSent = now;
                return true;
            }
        }

        public ulong NextSequence()
        {
            lock (_lock)
            {
                return ++_sequence;
            }
        }

        public void Acknowledge(ulong sequence)
        {
            lock (_lock)
            {
                if (sequence <= _sequence && sequence > _acknowledged)
                {
                    _acknowledged = sequence;
                }
            }
        }

        public void ForceNextFrame()
        {
            lock (_lock)
            {
                _force = true;
            }
        }
    }
}
