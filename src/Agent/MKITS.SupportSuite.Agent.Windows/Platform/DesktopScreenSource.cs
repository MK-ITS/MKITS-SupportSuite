// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Drawing.Imaging;
using System.Globalization;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Platform.Capture;
using MKITS.SupportSuite.Agent.Windows.Platform.MediaFoundation;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>
/// The screen of the desktop this process runs on: captured with DXGI Desktop Duplication where possible, otherwise
/// with GDI (8.1), and sent as JPEG or as H.264 streams through Media Foundation (one encoder per stream, ADR-0027).
/// One image per monitor is shared by all viewers and codecs. The secure desktop (UAC, lock screen) cannot be captured
/// from a user process – the service helper (Phase 8.4) covers that.
/// </summary>
public sealed class DesktopScreenSource : IScreenSource, IDisposable
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly IReadOnlyList<IDesktopGrabber> _grabbers;
    private readonly Func<IReadOnlyList<DesktopDisplay>> _displays;
    private readonly Dictionary<uint, FrameBuffer> _frames = [];
    private readonly Dictionary<string, (H264Encoder Encoder, int Quality)> _encoders = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _disposed;

    public DesktopScreenSource()
        : this([new DxgiGrabber(), new GdiGrabber()], Displays)
    {
    }

    /// <param name="grabbers">Capture methods, preferred first.</param>
    /// <param name="displays">The monitors (internal for tests).</param>
    internal DesktopScreenSource(IReadOnlyList<IDesktopGrabber> grabbers, Func<IReadOnlyList<DesktopDisplay>> displays)
    {
        _grabbers = grabbers;
        _displays = displays;
    }

    public IReadOnlyCollection<VideoCodec> StreamCodecs => H264Encoder.IsAvailable ? [VideoCodec.H264] : [];

    public IReadOnlyList<MonitorDescriptor> GetMonitors() => _displays().Select(d => d.Monitor).ToList();

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Grab(monitorId) is not { } frame)
            {
                return null;
            }

            using var stream = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            frame.Bitmap.Save(stream, JpegCodec, parameters);
            return new EncodedFrame(VideoCodec.Jpeg, frame.Width, frame.Height, stream.ToArray(), Keyframe: true);
        }
    }

    public EncodedFrame? CaptureStream(uint monitorId, string streamKey, VideoCodec codec, int quality, bool keyframe, CancellationToken cancellationToken)
    {
        if (codec != VideoCodec.H264)
        {
            return null;
        }

        lock (_gate)
        {
            if (Grab(monitorId) is not { } frame)
            {
                return null;
            }

            var width = frame.Width & ~1; // H.264 (NV12) needs even sizes; an odd last column/row is left out
            var height = frame.Height & ~1;
            if (width == 0 || height == 0)
            {
                return null;
            }

            try
            {
                // A new encoder starts with a keyframe: used for keyframe requests and size or quality changes.
                if (keyframe || !_encoders.TryGetValue(streamKey, out var current) || current.Encoder.Width != width
                    || current.Encoder.Height != height || current.Quality != quality)
                {
                    ReleaseStream(streamKey);
                    current = (new H264Encoder(width, height, Bitrate(width, height, quality)), quality);
                    _encoders[streamKey] = current;
                }

                var unit = current.Encoder.Encode(frame.Pixels, frame.Stride, out var isKeyframe);
                return unit is null ? null : new EncodedFrame(VideoCodec.H264, width, height, unit, isKeyframe);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Encoder failure (e.g. out of resources): drop it; the streamer asks for a keyframe on the next tick.
                ReleaseStream(streamKey);
                return null;
            }
        }
    }

    public void ReleaseStream(string streamKey)
    {
        lock (_gate)
        {
            if (_encoders.Remove(streamKey, out var stream))
            {
                stream.Encoder.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var frame in _frames.Values)
            {
                frame.Dispose();
            }

            _frames.Clear();
            foreach (var (encoder, _) in _encoders.Values)
            {
                encoder.Dispose();
            }

            _encoders.Clear();
            foreach (var grabber in _grabbers)
            {
                grabber.Dispose();
            }
        }
    }

    /// <summary>
    /// Bits per second: screen content compresses well; about 2 bits per pixel and second at normal quality
    /// (1080p ≈ 4 Mbit/s), twice that when the viewer prefers quality.
    /// </summary>
    internal static int Bitrate(int width, int height, int quality) =>
        (int)Math.Clamp((long)width * height * (quality >= 80 ? 4 : 2), 500_000, 40_000_000);

    /// <summary>The monitors as Windows reports them, numbered from 1 in its order.</summary>
    private static IReadOnlyList<DesktopDisplay> Displays() =>
        Screen.AllScreens
            .Select((screen, index) => new DesktopDisplay(
                new MonitorDescriptor(
                    (uint)(index + 1),
                    (index + 1).ToString(CultureInfo.InvariantCulture), // the viewer shows "primary" itself (localized)
                    screen.Bounds.Left,
                    screen.Bounds.Top,
                    screen.Bounds.Width,
                    screen.Bounds.Height,
                    screen.Primary),
                screen.DeviceName))
            .ToList();

    /// <summary>The current image of a monitor, from the first grabber that can deliver it (caller holds the lock).</summary>
    private FrameBuffer? Grab(uint monitorId)
    {
        var display = _displays().FirstOrDefault(d => d.Monitor.Id == monitorId);
        if (_disposed || display is null || display.Monitor.Width <= 0 || display.Monitor.Height <= 0)
        {
            return null;
        }

        if (!_frames.TryGetValue(monitorId, out var frame))
        {
            frame = new FrameBuffer();
            _frames[monitorId] = frame;
        }

        frame.EnsureSize(display.Monitor.Width, display.Monitor.Height);
        foreach (var grabber in _grabbers)
        {
            if (grabber.Grab(display, frame))
            {
                return frame;
            }
        }

        return null;
    }
}
