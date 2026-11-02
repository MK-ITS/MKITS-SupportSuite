// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Video;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class VideoTests
{
    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]          // black
    [InlineData(255, 255, 255, 235, 128, 128)]   // white
    [InlineData(255, 0, 0, 81, 90, 240)]         // red: B, G, R order in memory
    [InlineData(0, 0, 255, 41, 240, 110)]        // blue
    public void Nv12_ConvertsBt601LimitedRange(byte r, byte g, byte b, byte expectedY, byte expectedU, byte expectedV)
    {
        const int Width = 4;
        const int Height = 2;
        var bgrx = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            bgrx[i * 4] = b;
            bgrx[(i * 4) + 1] = g;
            bgrx[(i * 4) + 2] = r;
        }

        var nv12 = new byte[Nv12.Size(Width, Height)];
        Nv12.FromBgrx(bgrx, Width * 4, Width, Height, nv12);

        Assert.All(nv12[..(Width * Height)], y => Assert.InRange(y, expectedY - 1, expectedY + 1));
        Assert.InRange(nv12[Width * Height], expectedU - 1, expectedU + 1);
        Assert.InRange(nv12[(Width * Height) + 1], expectedV - 1, expectedV + 1);
    }

    [Fact]
    public void Nv12_UsesTheStride_AndRejectsOddSizes()
    {
        var bgrx = new byte[(2 * 16) + 8]; // 2 rows of 16 bytes (2 pixels + padding)
        bgrx[16] = 255; // first pixel of the second row is white
        bgrx[17] = 255;
        bgrx[18] = 255;
        var nv12 = new byte[Nv12.Size(2, 2)];

        Nv12.FromBgrx(bgrx, 16, 2, 2, nv12);

        Assert.Equal(16, nv12[0]);
        Assert.InRange(nv12[2], 234, 236);
        Assert.Throws<ArgumentException>(() => Nv12.FromBgrx(bgrx, 16, 3, 2, new byte[100]));
        Assert.Throws<ArgumentException>(() => Nv12.FromBgrx(bgrx, 4, 2, 2, nv12)); // stride smaller than a row
    }

    [Fact]
    public void AnnexB_FindsNalUnitTypes()
    {
        byte[] keyframe = [0, 0, 0, 1, 0x67, 0x42, 0, 0x1E, 0, 0, 1, 0x68, 0xCE, 0, 0, 1, 0x65, 0x88];
        byte[] delta = [0, 0, 0, 1, 0x41, 0x9A];

        Assert.True(AnnexB.HasNal(keyframe, 7));
        Assert.True(AnnexB.HasNal(keyframe, 5));
        Assert.False(AnnexB.HasNal(delta, 7));
        Assert.True(AnnexB.HasNal(delta, 1));
    }
}
