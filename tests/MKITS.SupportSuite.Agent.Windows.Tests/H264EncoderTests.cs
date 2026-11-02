// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Video;
using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Agent.Windows.Platform.MediaFoundation;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

/// <summary>
/// Windows' H.264 encoder (Media Foundation). Windows Server without the Media Foundation feature has none – then the
/// agent offers JPEG only, and these tests only check that this is detected.
/// </summary>
public sealed class H264EncoderTests
{
    [Fact]
    public void Encoder_StartsWithAKeyframeCarryingSps_ThenDeltas()
    {
        if (!H264Encoder.IsAvailable)
        {
            using var source = new GdiScreenSource();
            Assert.Empty(source.StreamCodecs);
            return;
        }

        const int Width = 320;
        const int Height = 240;
        using var encoder = new H264Encoder(Width, Height, GdiScreenSource.Bitrate(Width, Height, 60));
        var frame = new byte[Width * Height * 4];
        var units = new List<(byte[] Data, bool Keyframe)>();
        for (var i = 0; i < 6; i++)
        {
            frame[i * 400] = 0xFF; // a little change per frame
            if (encoder.Encode(frame, Width * 4, out var keyframe) is { } unit)
            {
                units.Add((unit, keyframe));
            }
        }

        Assert.NotEmpty(units);
        Assert.True(units[0].Keyframe);
        Assert.True(AnnexB.HasNal(units[0].Data, 7)); // SPS: the browser builds its decoder configuration from it
        Assert.True(AnnexB.HasNal(units[0].Data, 5)); // IDR slice
        Assert.Contains(units.Skip(1), u => !u.Keyframe);
    }

    [Fact]
    public void Bitrate_ScalesWithSizeAndQuality_WithinLimits()
    {
        Assert.Equal(1920 * 1080 * 2, GdiScreenSource.Bitrate(1920, 1080, 60));
        Assert.Equal(1920 * 1080 * 4, GdiScreenSource.Bitrate(1920, 1080, 85));
        Assert.Equal(500_000, GdiScreenSource.Bitrate(64, 64, 60));
        Assert.Equal(40_000_000, GdiScreenSource.Bitrate(7680, 4320, 85));
    }
}
