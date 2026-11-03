// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Windows.Platform.Capture;

/// <summary>
/// The latest image of one monitor: 32-bit BGRX, top-down, rows without padding. The memory is pinned, so GDI can draw
/// into it and the JPEG and H.264 encoders read it without copies (<see cref="Bitmap"/> is a view, not a copy). Not
/// thread-safe; the screen source guards it.
/// </summary>
internal sealed class FrameBuffer : IDisposable
{
    private byte[] _pixels = [];
    private Bitmap? _bitmap;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    /// <summary>Whether a grabber filled the buffer since it got its current size (an unchanged screen may then be reused).</summary>
    public bool HasContent { get; set; }

    public Span<byte> Pixels => _pixels.AsSpan(0, Stride * Height);

    public Bitmap Bitmap => _bitmap ??= new Bitmap(Width, Height, Stride, PixelFormat.Format32bppRgb, Marshal.UnsafeAddrOfPinnedArrayElement(_pixels, 0));

    /// <summary>Reallocates for a new monitor size (and forgets the content).</summary>
    public void EnsureSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == Width && height == Height)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = null;
        _pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4), pinned: true);
        Width = width;
        Height = height;
        HasContent = false;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
