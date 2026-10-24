// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Linux.Native;

/// <summary>TurboJPEG API (libturbojpeg.so.0; Debian: libturbojpeg0, Ubuntu: libturbojpeg).</summary>
internal static partial class TurboJpegNative
{
    private const string Library = "libturbojpeg.so.0";

    internal const int PixelFormatRgbx = 2;
    internal const int PixelFormatBgrx = 3;
    internal const int Subsampling420 = 2;
    internal const int FlagFastDct = 2048;

    [LibraryImport(Library, EntryPoint = "tjInitCompress")]
    internal static partial nint InitCompress();

    [LibraryImport(Library, EntryPoint = "tjCompress2")]
    internal static partial int Compress2(
        nint handle, nint source, int width, int pitch, int height, int pixelFormat, ref nint jpegBuffer, ref nuint jpegSize,
        int subsampling, int quality, int flags);

    [LibraryImport(Library, EntryPoint = "tjFree")]
    internal static partial void Free(nint buffer);

    [LibraryImport(Library, EntryPoint = "tjDestroy")]
    internal static partial int Destroy(nint handle);
}
