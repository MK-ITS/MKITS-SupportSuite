// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Linux.Native;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>
/// Screen capture on X11 (<c>XGetImage</c> of the root window per monitor) with JPEG encoding (libjpeg-turbo).
/// Monitors come from XRandR; without it the whole screen is one monitor. Wayland sessions need the portal source.
/// </summary>
internal sealed class X11ScreenSource(X11Display display) : IScreenSource, IDisposable
{
    private readonly JpegEncoder _encoder = new();

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        lock (display.Gate)
        {
            var monitors = new List<MonitorDescriptor>();
            nint list = 0;
            try
            {
                list = X11Native.XRRGetMonitors(display.Handle, display.Root, getActive: true, out var count);
                for (var i = 0; i < count && list != 0; i++)
                {
                    var info = Marshal.PtrToStructure<X11Native.XRRMonitorInfo>(list + (i * Marshal.SizeOf<X11Native.XRRMonitorInfo>()));
                    monitors.Add(new MonitorDescriptor(
                        (uint)(i + 1), (i + 1).ToString(CultureInfo.InvariantCulture), info.X, info.Y, info.Width, info.Height, info.Primary != 0));
                }
            }
            catch (DllNotFoundException)
            {
                // no XRandR – fall back to the whole screen
            }
            finally
            {
                if (list != 0)
                {
                    X11Native.XRRFreeMonitors(list);
                }
            }

            if (monitors.Count == 0)
            {
                monitors.Add(new MonitorDescriptor(
                    1, "1", 0, 0, X11Native.XDisplayWidth(display.Handle, display.Screen), X11Native.XDisplayHeight(display.Handle, display.Screen), true));
            }
            else if (!monitors.Any(m => m.Primary))
            {
                monitors[0] = monitors[0] with { Primary = true };
            }

            return monitors;
        }
    }

    public EncodedFrame? Capture(uint monitorId, int quality, CancellationToken cancellationToken)
    {
        var monitor = GetMonitors().FirstOrDefault(m => m.Id == monitorId);
        if (monitor is null || monitor.Width <= 0 || monitor.Height <= 0)
        {
            return null;
        }

        lock (display.Gate)
        {
            var image = X11Native.XGetImage(
                display.Handle, display.Root, monitor.Left, monitor.Top, (uint)monitor.Width, (uint)monitor.Height, X11Native.AllPlanes, X11Native.ZPixmap);
            if (image == 0)
            {
                return null;
            }

            try
            {
                var info = Marshal.PtrToStructure<X11Native.XImage>(image);
                if (info.BitsPerPixel != 32 || info.Data == 0)
                {
                    return null; // 16-bit and paletted visuals are not supported
                }

                var bgrx = info.RedMask == 0xFF0000 && info.BlueMask == 0xFF && info.ByteOrder == 0 /* LSBFirst */;
                var jpeg = _encoder.Encode(info.Data, info.Width, info.Height, info.BytesPerLine, bgrx, quality);
                return jpeg is null ? null : new EncodedFrame(VideoCodec.Jpeg, info.Width, info.Height, jpeg, Keyframe: true);
            }
            finally
            {
                _ = X11Native.XDestroyImage(image);
            }
        }
    }

    public void Dispose() => _encoder.Dispose();
}
