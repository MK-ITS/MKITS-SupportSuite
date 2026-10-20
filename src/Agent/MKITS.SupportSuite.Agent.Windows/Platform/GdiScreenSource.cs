// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Drawing.Imaging;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Platform;

/// <summary>
/// Screen capture with GDI (<c>BitBlt</c> via <see cref="Graphics.CopyFromScreen(int, int, int, int, Size)"/>) and JPEG
/// encoding. Works on every Windows version and in RDP sessions; the DXGI + H.264 path (Phase 8.1/8.2) replaces it
/// where available. The secure desktop (UAC, lock screen) cannot be captured from a user process – the service
/// helper (Phase 8.4) covers that.
/// </summary>
public sealed class GdiScreenSource : IScreenSource, IDisposable
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly Dictionary<uint, Bitmap> _buffers = [];

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

    public void Dispose()
    {
        foreach (var buffer in _buffers.Values)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
    }

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
