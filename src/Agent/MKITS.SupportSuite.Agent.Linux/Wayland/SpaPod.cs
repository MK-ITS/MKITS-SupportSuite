// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>A negotiated raw video format.</summary>
internal sealed record VideoFormat(uint Format, int Width, int Height)
{
    /// <summary>Blue in the lowest byte (BGRx/BGRA); otherwise RGBx/RGBA.</summary>
    public bool Bgrx => Format is SpaPod.VideoFormatBgrx or SpaPod.VideoFormatBgra;

    public bool IsSupported => Format is SpaPod.VideoFormatBgrx or SpaPod.VideoFormatBgra or SpaPod.VideoFormatRgbx or SpaPod.VideoFormatRgba;
}

/// <summary>
/// The few SPA POD structures PipeWire format negotiation needs (spa/pod/pod.h – the C builder macros are inline, so
/// they cannot be called): every POD is a 32-bit size + 32-bit type header, bodies padded to 8 bytes, little endian.
/// </summary>
internal static class SpaPod
{
    // spa/utils/type.h, spa/param/*.h
    internal const uint TypeId = 3;
    internal const uint TypeRectangle = 10;
    internal const uint TypeFraction = 11;
    internal const uint TypeObject = 15;
    internal const uint TypeChoice = 19;
    internal const uint ObjectFormat = 0x40003;
    internal const uint ParamEnumFormat = 3;
    internal const uint ParamFormat = 4;
    internal const uint ChoiceNone = 0;
    internal const uint ChoiceRange = 1;
    internal const uint ChoiceEnum = 3;
    internal const uint KeyMediaType = 1;
    internal const uint KeyMediaSubtype = 2;
    internal const uint KeyVideoFormat = 0x20001;
    internal const uint KeyVideoSize = 0x20003;
    internal const uint KeyVideoFramerate = 0x20004;
    internal const uint MediaTypeVideo = 2;
    internal const uint MediaSubtypeRaw = 1;
    internal const uint VideoFormatRgbx = 7;
    internal const uint VideoFormatBgrx = 8;
    internal const uint VideoFormatRgba = 11;
    internal const uint VideoFormatBgra = 12;

    /// <summary>
    /// What the agent can take: raw video in a 32-bit RGB format (no DMA-BUF modifiers, so the compositor sends shared
    /// memory), any size up to 8192², any frame rate.
    /// </summary>
    public static byte[] EnumFormat()
    {
        var properties = new List<byte>();
        Property(properties, KeyMediaType, Id(MediaTypeVideo));
        Property(properties, KeyMediaSubtype, Id(MediaSubtypeRaw));
        Property(properties, KeyVideoFormat, Choice(ChoiceEnum, TypeId, 4,
            U32(VideoFormatBgrx), U32(VideoFormatBgrx), U32(VideoFormatRgbx), U32(VideoFormatBgra), U32(VideoFormatRgba)));
        Property(properties, KeyVideoSize, Choice(ChoiceRange, TypeRectangle, 8, Pair(1920, 1080), Pair(1, 1), Pair(8192, 8192)));
        Property(properties, KeyVideoFramerate, Choice(ChoiceRange, TypeFraction, 8, Pair(30, 1), Pair(0, 1), Pair(1000, 1)));

        var body = new List<byte>();
        body.AddRange(U32(ObjectFormat));
        body.AddRange(U32(ParamEnumFormat));
        body.AddRange(properties);
        return Pod(TypeObject, body);
    }

    /// <summary>Reads format and size from a negotiated <c>SPA_PARAM_Format</c> object; <c>null</c> if incomplete or malformed.</summary>
    public static VideoFormat? ParseFormat(ReadOnlySpan<byte> pod)
    {
        if (pod.Length < 16)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(pod);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(pod[4..]);
        if (type != TypeObject || size < 8 || size > pod.Length - 8 || BinaryPrimitives.ReadUInt32LittleEndian(pod[8..]) != ObjectFormat)
        {
            return null;
        }

        uint? format = null;
        (int Width, int Height)? dimensions = null;
        var offset = 16;
        var end = 8 + (int)size;
        while (offset + 16 <= end)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(pod[offset..]);
            var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(pod[(offset + 8)..]);
            var valueType = BinaryPrimitives.ReadUInt32LittleEndian(pod[(offset + 12)..]);
            var valueStart = offset + 16;
            if (valueSize > end - valueStart)
            {
                return null;
            }

            var value = pod.Slice(valueStart, (int)valueSize);
            if (valueType == TypeChoice && value.Length >= 16)
            {
                // A fixed choice (or the default of a range/enum): the first child value.
                var childSize = BinaryPrimitives.ReadUInt32LittleEndian(value[8..]);
                valueType = BinaryPrimitives.ReadUInt32LittleEndian(value[12..]);
                value = value.Length >= 16 + childSize ? value.Slice(16, (int)childSize) : [];
            }

            if (key == KeyVideoFormat && valueType == TypeId && value.Length >= 4)
            {
                format = BinaryPrimitives.ReadUInt32LittleEndian(value);
            }
            else if (key == KeyVideoSize && valueType == TypeRectangle && value.Length >= 8)
            {
                dimensions = ((int)BinaryPrimitives.ReadUInt32LittleEndian(value), (int)BinaryPrimitives.ReadUInt32LittleEndian(value[4..]));
            }

            offset = valueStart + Align((int)valueSize);
        }

        return format is { } f && dimensions is { Width: > 0 and <= 16384, Height: > 0 and <= 16384 } d ? new VideoFormat(f, d.Width, d.Height) : null;
    }

    private static int Align(int length) => (length + 7) & ~7;

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Pair(uint a, uint b) => [.. U32(a), .. U32(b)];

    private static byte[] Id(uint value) => Pod(TypeId, U32(value));

    /// <summary>Choice body: type, flags, child header (size, type), then the values.</summary>
    private static byte[] Choice(uint choiceType, uint childType, uint childSize, params byte[][] values)
    {
        var body = new List<byte>();
        body.AddRange(U32(choiceType));
        body.AddRange(U32(0));
        body.AddRange(U32(childSize));
        body.AddRange(U32(childType));
        foreach (var value in values)
        {
            body.AddRange(value);
        }

        return Pod(TypeChoice, body);
    }

    private static void Property(List<byte> target, uint key, byte[] value)
    {
        target.AddRange(U32(key));
        target.AddRange(U32(0)); // flags
        target.AddRange(value);
    }

    private static byte[] Pod(uint type, IReadOnlyCollection<byte> body)
    {
        var pod = new byte[8 + Align(body.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(pod, (uint)body.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(pod.AsSpan(4), type);
        var i = 8;
        foreach (var b in body)
        {
            pod[i++] = b;
        }

        return pod;
    }
}
