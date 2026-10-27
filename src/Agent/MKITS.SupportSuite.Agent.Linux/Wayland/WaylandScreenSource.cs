// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>
/// The screens the user shared in the portal dialog, one PipeWire stream each. Monitor geometry is the stream's
/// logical position and size (what input coordinates refer to); frames have the stream's pixel size, which differs
/// with display scaling.
/// </summary>
internal sealed class WaylandScreenSource(IReadOnlyList<(PortalStream Stream, PipeWireCapture Capture)> streams) : IScreenSource, IDisposable
{
    private readonly JpegEncoder _encoder = new();
    private readonly Lock _encoderLock = new();

    public IReadOnlyList<MonitorDescriptor> GetMonitors() =>
        streams.Select((s, i) => new MonitorDescriptor(
            (uint)(i + 1), (i + 1).ToString(CultureInfo.InvariantCulture), s.Stream.X, s.Stream.Y, s.Stream.Width, s.Stream.Height, Primary: i == 0))
            .ToList();

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken)
    {
        if (monitorId < 1 || monitorId > streams.Count)
        {
            return null;
        }

        var capture = streams[(int)monitorId - 1].Capture;
        lock (_encoderLock)
        {
            return capture.WithLatestFrame<EncodedFrame?>(
                (pixels, width, height, stride, bgrx) => _encoder.Encode(pixels, width, height, stride, bgrx, quality) is { } jpeg
                    ? new EncodedFrame(VideoCodec.Jpeg, width, height, jpeg, Keyframe: true)
                    : null,
                null);
        }
    }

    public void Dispose()
    {
        foreach (var (_, capture) in streams)
        {
            capture.Dispose();
        }

        lock (_encoderLock)
        {
            _encoder.Dispose();
        }
    }
}
