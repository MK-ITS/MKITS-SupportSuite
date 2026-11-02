// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Core.Video;

/// <summary>
/// BGRX → NV12 (the input format of hardware and software H.264 encoders): a full-resolution luma plane followed by
/// interleaved U/V at half resolution. BT.601 limited range – what H.264 decoders assume when the stream says nothing.
/// </summary>
public static class Nv12
{
    /// <summary>Bytes needed for an NV12 image of an even <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static int Size(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// Converts the top-left <paramref name="width"/> × <paramref name="height"/> pixels (both even) of a BGRX image with
    /// <paramref name="stride"/> bytes per row into <paramref name="nv12"/>.
    /// </summary>
    public static void FromBgrx(ReadOnlySpan<byte> bgrx, int stride, int width, int height, Span<byte> nv12)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("Width and height must be positive and even.");
        }

        if (stride < width * 4 || bgrx.Length < ((long)stride * (height - 1)) + (width * 4) || nv12.Length < Size(width, height))
        {
            throw new ArgumentException("Buffers are too small for the image.");
        }

        var luma = nv12[..(width * height)];
        var chroma = nv12[(width * height)..];
        for (var y = 0; y < height; y += 2)
        {
            var row0 = bgrx.Slice(y * stride, width * 4);
            var row1 = bgrx.Slice((y + 1) * stride, width * 4);
            var y0 = luma.Slice(y * width, width);
            var y1 = luma.Slice((y + 1) * width, width);
            var uv = chroma.Slice(y / 2 * width, width);
            for (var x = 0; x < width; x += 2)
            {
                int b = 0, g = 0, r = 0;
                Pixel(row0, x, y0, ref b, ref g, ref r);
                Pixel(row0, x + 1, y0, ref b, ref g, ref r);
                Pixel(row1, x, y1, ref b, ref g, ref r);
                Pixel(row1, x + 1, y1, ref b, ref g, ref r);

                // average of the 2×2 block (sums of four, hence the extra shift by 2)
                uv[x] = Clamp(((((-38 * r) - (74 * g) + (112 * b)) >> 2) + 128 + (128 << 8)) >> 8);
                uv[x + 1] = Clamp(((((112 * r) - (94 * g) - (18 * b)) >> 2) + 128 + (128 << 8)) >> 8);
            }
        }
    }

    private static void Pixel(ReadOnlySpan<byte> row, int x, Span<byte> luma, ref int b, ref int g, ref int r)
    {
        var pb = row[x * 4];
        var pg = row[(x * 4) + 1];
        var pr = row[(x * 4) + 2];
        luma[x] = (byte)((((66 * pr) + (129 * pg) + (25 * pb) + 128) >> 8) + 16);
        b += pb;
        g += pg;
        r += pr;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
