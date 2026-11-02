// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Core.Video;

/// <summary>H.264 Annex B byte streams (NAL units separated by 00 00 01 / 00 00 00 01 start codes).</summary>
public static class AnnexB
{
    /// <summary>Whether the access unit contains a NAL unit of <paramref name="type"/> (5 = IDR slice, 7 = SPS, 8 = PPS).</summary>
    public static bool HasNal(ReadOnlySpan<byte> data, int type)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if ((data[i + 3] & 0x1F) == type)
                {
                    return true;
                }

                i += 2;
            }
        }

        return false;
    }
}
