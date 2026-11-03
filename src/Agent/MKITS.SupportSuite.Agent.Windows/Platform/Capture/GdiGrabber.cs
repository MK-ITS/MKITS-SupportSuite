// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Windows.Platform.Capture;

/// <summary>
/// GDI <c>BitBlt</c> (<see cref="Graphics.CopyFromScreen(int, int, int, int, Size)"/>): slower than DXGI, but works
/// everywhere – RDP sessions, rotated monitors, old drivers.
/// </summary>
internal sealed class GdiGrabber : IDesktopGrabber
{
    public bool Grab(DesktopDisplay display, FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(frame);
        var monitor = display.Monitor;
        try
        {
            using var graphics = Graphics.FromImage(frame.Bitmap);
            graphics.CopyFromScreen(monitor.Left, monitor.Top, 0, 0, new Size(frame.Width, frame.Height), CopyPixelOperation.SourceCopy);
            frame.HasContent = true;
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // e.g. the secure desktop is active or the session is locked
        }
    }

    public void Dispose()
    {
    }
}
