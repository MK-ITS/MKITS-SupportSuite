// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>JPEG encoding with libjpeg-turbo (4:2:0, fast DCT). Not thread-safe; one encoder per capture source.</summary>
internal sealed class JpegEncoder : IDisposable
{
    private nint _handle;

    /// <summary><c>true</c> if libturbojpeg is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                var handle = TurboJpegNative.InitCompress();
                if (handle == 0)
                {
                    return false;
                }

                _ = TurboJpegNative.Destroy(handle);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Encodes 32-bit pixels (<c>bgrx</c>: blue in the lowest byte, as X servers deliver on little-endian machines).</summary>
    public byte[]? Encode(nint pixels, int width, int height, int stride, bool bgrx, int quality)
    {
        if (_handle == 0)
        {
            _handle = TurboJpegNative.InitCompress();
            if (_handle == 0)
            {
                return null;
            }
        }

        nint buffer = 0;
        nuint size = 0;
        try
        {
            var result = TurboJpegNative.Compress2(
                _handle, pixels, width, stride, height, bgrx ? TurboJpegNative.PixelFormatBgrx : TurboJpegNative.PixelFormatRgbx,
                ref buffer, ref size, TurboJpegNative.Subsampling420, Math.Clamp(quality, 1, 100), TurboJpegNative.FlagFastDct);
            if (result != 0 || buffer == 0)
            {
                return null;
            }

            var jpeg = new byte[(int)size];
            System.Runtime.InteropServices.Marshal.Copy(buffer, jpeg, 0, jpeg.Length);
            return jpeg;
        }
        finally
        {
            if (buffer != 0)
            {
                TurboJpegNative.Free(buffer);
            }
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            _ = TurboJpegNative.Destroy(_handle);
            _handle = 0;
        }
    }
}
