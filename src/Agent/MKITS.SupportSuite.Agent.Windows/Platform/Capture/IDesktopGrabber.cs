// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Features;

namespace MKITS.SupportSuite.Agent.Windows.Platform.Capture;

/// <summary>A monitor as Windows names it (<c>\\.\DISPLAY1</c>) – how GDI and DXGI outputs are matched.</summary>
internal sealed record DesktopDisplay(MonitorDescriptor Monitor, string DeviceName);

/// <summary>One way to copy a monitor's image into a <see cref="FrameBuffer"/> (DXGI Desktop Duplication, GDI).</summary>
internal interface IDesktopGrabber : IDisposable
{
    /// <summary>
    /// Fills <paramref name="frame"/> (already sized to the monitor) with the current image. <c>false</c> if this way
    /// cannot capture the monitor right now – the screen source then tries the next one.
    /// </summary>
    bool Grab(DesktopDisplay display, FrameBuffer frame);
}
