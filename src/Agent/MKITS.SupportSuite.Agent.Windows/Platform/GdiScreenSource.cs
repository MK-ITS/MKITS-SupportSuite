// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Drawing.Imaging;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Platform.MediaFoundation;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>
/// Screen capture with GDI (<c>BitBlt</c> via <see cref="Graphics.CopyFromScreen(int, int, int, int, Size)"/>) and JPEG
/// encoding, or H.264 streams through Media Foundation (one encoder per stream, ADR-0027) where Windows has the
/// encoder. Works on every Windows version and in RDP sessions; DXGI capture (Phase 8.1) follows. The secure desktop
/// (UAC, lock screen) cannot be captured from a user process – the service helper (Phase 8.4) covers that.
/// </summary>
public sealed class GdiScreenSource : IScreenSource, IDisposable
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly Dictionary<uint, Bitmap> _buffers = [];
    private readonly Dictionary<uint, Bitmap> _streamBuffers = [];
    private readonly Dictionary<string, (H264Encoder Encoder, int Quality)> _encoders = new(StringComparer.Ordinal);
    private readonly Lock _streamLock = new();

    public IReadOnlyCollection<VideoCodec> StreamCodecs => H264Encoder.IsAvailable ? [VideoCodec.H264] : [];

    public IReadOnlyList<MonitorDescriptor> GetMonitors() =>
        Screen.AllScreens
            .Select((screen, index) => new MonitorDescriptor(
                (uint)(index + 1),
                (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), // the viewer shows "primary" itself (localized)
                screen.Bounds.Left,
                screen.Bounds.Top,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Primary))
            .ToList();

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken)
    {
        var monitor = GetMonitors().FirstOrDefault(m => m.Id == monitorId);
        if (monitor is null || monitor.Width <= 0 || monitor.Height <= 0)
        {
            return null;
        }

        var bitmap = GetBuffer(monitor);
        try
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, new Size(monitor.Width, monitor.Height), CopyPixelOperation.SourceCopy);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // e.g. the secure desktop is active or the session is locked
        }

        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bitmap.Save(stream, JpegCodec, parameters);
        return new EncodedFrame(VideoCodec.Jpeg, monitor.Width, monitor.Height, stream.ToArray(), Keyframe: true);
    }

    public EncodedFrame? CaptureStream(uint monitorId, string streamKey, VideoCodec codec, int quality, bool keyframe, CancellationToken cancellationToken)
    {
        if (codec != VideoCodec.H264)
        {
            return null;
        }

        var monitor = GetMonitors().FirstOrDefault(m => m.Id == monitorId);
        var width = (monitor?.Width ?? 0) & ~1; // H.264 (NV12) needs even sizes; an odd last column/row is left out
        var height = (monitor?.Height ?? 0) & ~1;
        if (monitor is null || width <= 0 || height <= 0)
        {
            return null;
        }

        lock (_streamLock)
        {
            if (!_streamBuffers.TryGetValue(monitor.Id, out var bitmap) || bitmap.Width != monitor.Width || bitmap.Height != monitor.Height)
            {
                bitmap?.Dispose();
                bitmap = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format32bppRgb);
                _streamBuffers[monitor.Id] = bitmap;
            }

            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, new Size(monitor.Width, monitor.Height), CopyPixelOperation.SourceCopy);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null; // secure desktop or locked session
            }

            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
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

                unsafe
                {
                    var pixels = new ReadOnlySpan<byte>((void*)data.Scan0, data.Stride * bitmap.Height);
                    var unit = current.Encoder.Encode(pixels, data.Stride, out var isKeyframe);
                    return unit is null ? null : new EncodedFrame(VideoCodec.H264, width, height, unit, isKeyframe);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Encoder failure (e.g. out of resources): drop it; the streamer asks for a keyframe on the next tick.
                ReleaseStream(streamKey);
                return null;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }

    public void ReleaseStream(string streamKey)
    {
        lock (_streamLock)
        {
            if (_encoders.Remove(streamKey, out var stream))
            {
                stream.Encoder.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        lock (_streamLock)
        {
            foreach (var buffer in _streamBuffers.Values)
            {
                buffer.Dispose();
            }

            _streamBuffers.Clear();
            foreach (var (encoder, _) in _encoders.Values)
            {
                encoder.Dispose();
            }

            _encoders.Clear();
        }
    }

    /// <summary>
    /// Bits per second: screen content compresses well; about 2 bits per pixel and second at normal quality
    /// (1080p ≈ 4 Mbit/s), twice that when the viewer prefers quality.
    /// </summary>
    internal static int Bitrate(int width, int height, int quality) =>
        (int)Math.Clamp((long)width * height * (quality >= 80 ? 4 : 2), 500_000, 40_000_000);

    /// <summary>Reuses one bitmap per monitor (re-created when the resolution changes).</summary>
    private Bitmap GetBuffer(MonitorDescriptor monitor)
    {
        if (_buffers.TryGetValue(monitor.Id, out var existing) && existing.Width == monitor.Width && existing.Height == monitor.Height)
        {
            return existing;
        }

        existing?.Dispose();
        var bitmap = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format24bppRgb);
        _buffers[monitor.Id] = bitmap;
        return bitmap;
    }
}
