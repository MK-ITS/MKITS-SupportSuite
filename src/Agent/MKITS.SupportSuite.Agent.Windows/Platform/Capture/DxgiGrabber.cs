// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using static MKITS.SupportSuite.Agent.Windows.Native.DxgiNative;

namespace MKITS.SupportSuite.Agent.Windows.Platform.Capture;

/// <summary>
/// DXGI Desktop Duplication: the compositor hands over its image of a monitor, much cheaper than GDI and including
/// hardware-accelerated content. One duplication per monitor, created on first use. Not available in some situations
/// (RDP sessions, basic display drivers, rotated monitors, a DPI-virtualized process) – then a monitor is left to GDI
/// for a while before DXGI is tried again. The duplication is lost on desktop switches (UAC, lock screen); it is
/// recreated on the next frame.
/// </summary>
internal sealed class DxgiGrabber(TimeProvider? timeProvider = null) : IDesktopGrabber
{
    /// <summary>How long a monitor stays with GDI after DXGI could not be set up for it.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Duplication> _duplications = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _unavailableUntil = new(StringComparer.OrdinalIgnoreCase);

    public bool Grab(DesktopDisplay display, FrameBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(frame);
        var name = display.DeviceName;
        if (_unavailableUntil.TryGetValue(name, out var until) && _time.GetUtcNow() < until)
        {
            return false;
        }

        if (!_duplications.TryGetValue(name, out var duplication))
        {
            duplication = Duplication.TryCreate(name);
            if (duplication is null)
            {
                _unavailableUntil[name] = _time.GetUtcNow() + RetryAfter;
                return false;
            }

            _duplications[name] = duplication;
        }

        var result = duplication.Grab(frame);
        if (result == GrabResult.Ok)
        {
            return true;
        }

        duplication.Dispose();
        _duplications.Remove(name);
        if (result == GrabResult.Failed)
        {
            _unavailableUntil[name] = _time.GetUtcNow() + RetryAfter;
        }

        return false; // Lost (desktop switch, mode change): recreated on the next frame, GDI covers this one
    }

    public void Dispose()
    {
        foreach (var duplication in _duplications.Values)
        {
            duplication.Dispose();
        }

        _duplications.Clear();
    }

    private enum GrabResult
    {
        Ok,
        Lost,
        Failed,
    }

    /// <summary>The duplication of one output with its device and a CPU-readable copy of the desktop texture.</summary>
    private sealed unsafe class Duplication : IDisposable
    {
        private const uint FirstFrameTimeoutMilliseconds = 100;

        private nint _device;
        private nint _context;
        private nint _duplication;
        private nint _staging;

        private Duplication(nint device, nint context, nint duplication, nint staging, int width, int height)
        {
            _device = device;
            _context = context;
            _duplication = duplication;
            _staging = staging;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Sets up the duplication of the output named <paramref name="deviceName"/>; <c>null</c> if that is not possible.</summary>
        public static Duplication? TryCreate(string deviceName)
        {
            nint factory = 0;
            try
            {
                if (CreateFactory1(IidFactory1, out factory) < 0)
                {
                    return null;
                }

                for (uint a = 0; EnumAdapters1(factory, a, out var adapter) >= 0; a++)
                {
                    try
                    {
                        for (uint o = 0; EnumOutputs(adapter, o, out var output) >= 0; o++)
                        {
                            try
                            {
                                if (GetOutputDesc(output, out var desc) >= 0 && desc.AttachedToDesktop != 0
                                    && string.Equals(desc.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Rotated outputs deliver an unrotated texture; GDI handles them.
                                    return desc.Rotation is RotationUnspecified or RotationIdentity ? Open(adapter, output) : null;
                                }
                            }
                            finally
                            {
                                Release(ref output);
                            }
                        }
                    }
                    finally
                    {
                        Release(ref adapter);
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null; // no DXGI 1.2 (very old or stripped-down Windows)
            }
            finally
            {
                Release(ref factory);
            }
        }

        /// <summary>
        /// Copies the newest desktop image into <paramref name="frame"/>. No new image within the timeout means the
        /// screen did not change: the frame keeps what it has (a GDI fallback image included).
        /// </summary>
        public GrabResult Grab(FrameBuffer frame)
        {
            if (frame.Width != Width || frame.Height != Height)
            {
                return GrabResult.Failed; // the process sees scaled coordinates, or the mode is changing
            }

            var hr = AcquireNextFrame(_duplication, frame.HasContent ? 0 : FirstFrameTimeoutMilliseconds, out var info, out var resource);
            if (hr == WaitTimeout)
            {
                return frame.HasContent ? GrabResult.Ok : GrabResult.Lost;
            }

            if (hr < 0)
            {
                return hr == AccessLost ? GrabResult.Lost : GrabResult.Failed;
            }

            try
            {
                if (info.LastPresentTime == 0 && frame.HasContent)
                {
                    return GrabResult.Ok; // only the mouse moved
                }

                return CopyToFrame(resource, frame);
            }
            finally
            {
                Release(ref resource);
                ReleaseFrame(_duplication);
            }
        }

        public void Dispose()
        {
            Release(ref _staging);
            Release(ref _duplication);
            Release(ref _context);
            Release(ref _device);
        }

        private static Duplication? Open(nint adapter, nint output)
        {
            nint output1 = 0, device = 0, context = 0, duplication = 0, staging = 0;
            try
            {
                if (QueryInterface(output, IidOutput1, out output1) < 0
                    || CreateDevice(adapter, DriverTypeUnknown, 0, 0, 0, 0, SdkVersion, out device, out _, out context) < 0
                    || DuplicateOutput(output1, device, out duplication) < 0)
                {
                    return null; // e.g. E_ACCESSDENIED, DXGI_ERROR_UNSUPPORTED (RDP), too many duplications
                }

                GetDuplicationDesc(duplication, out var desc);
                if (desc.Width == 0 || desc.Height == 0 || desc.Rotation is not (RotationUnspecified or RotationIdentity))
                {
                    return null;
                }

                var stagingDesc = new Texture2DDesc
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = FormatB8G8R8A8,
                    SampleCount = 1,
                    Usage = UsageStaging,
                    CpuAccessFlags = CpuAccessRead,
                };
                if (CreateTexture2D(device, stagingDesc, out staging) < 0)
                {
                    return null;
                }

                var result = new Duplication(device, context, duplication, staging, (int)desc.Width, (int)desc.Height);
                device = context = duplication = staging = 0; // owned by the result now
                return result;
            }
            finally
            {
                Release(ref staging);
                Release(ref duplication);
                Release(ref context);
                Release(ref device);
                Release(ref output1);
            }
        }

        private GrabResult CopyToFrame(nint resource, FrameBuffer frame)
        {
            if (QueryInterface(resource, IidTexture2D, out var texture) < 0)
            {
                return GrabResult.Failed;
            }

            try
            {
                CopyResource(_context, _staging, texture);
            }
            finally
            {
                Release(ref texture);
            }

            if (Map(_context, _staging, out var mapped) < 0)
            {
                return GrabResult.Lost;
            }

            try
            {
                var rowBytes = frame.Stride;
                if (mapped.RowPitch < rowBytes)
                {
                    return GrabResult.Failed;
                }

                fixed (byte* target = frame.Pixels)
                {
                    for (var y = 0; y < frame.Height; y++)
                    {
                        Buffer.MemoryCopy((byte*)mapped.Data + ((long)y * mapped.RowPitch), target + ((long)y * rowBytes), rowBytes, rowBytes);
                    }
                }

                frame.HasContent = true;
                return GrabResult.Ok;
            }
            finally
            {
                Unmap(_context, _staging);
            }
        }
    }
}
