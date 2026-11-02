// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Video;
using MKITS.SupportSuite.Agent.Windows.Native;
using static MKITS.SupportSuite.Agent.Windows.Native.MediaFoundationNative;

namespace MKITS.SupportSuite.Agent.Windows.Platform.MediaFoundation;

/// <summary>
/// H.264 encoding with Windows' own software encoder (Media Foundation H.264 encoder MFT, licensed with the operating
/// system; ADR-0027). Constrained baseline profile, low-latency mode (one output per input, no B-frames), Annex B
/// output with SPS/PPS before every keyframe. One instance per stream; a keyframe is forced by starting a new encoder
/// (rare: new viewer, monitor change, lost frames). Not thread-safe.
/// </summary>
internal sealed class H264Encoder : IDisposable
{
    private const long FrameDuration = 10_000_000 / 30; // 100-ns units at the nominal 30 fps
    private const int MaxStreamChanges = 3;

    private static readonly Lazy<bool> Started = new(() =>
    {
        try
        {
            return Startup(MfVersion, MfStartupLite) >= 0;
        }
        catch (DllNotFoundException)
        {
            return false; // Windows Server without the Media Foundation feature
        }
    });

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var probe = new H264Encoder(64, 64, 1_000_000);
            var frame = new byte[64 * 64 * 4];
            for (var i = 0; i < 3; i++)
            {
                if (probe.Encode(frame, 64 * 4) is { Length: > 0 } unit && AnnexB.HasNal(unit, 7))
                {
                    return true; // a keyframe with SPS: the stream can be decoded
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or DllNotFoundException)
        {
            return false;
        }
    });

    private readonly IMFTransform _transform;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _nv12;
    private readonly int _outputSize;
    private byte[]? _sequenceHeader;
    private long _time;
    private bool _disposed;

    /// <param name="width">Even width (callers crop odd sizes).</param>
    /// <param name="height">Even height.</param>
    /// <param name="bitrate">Average bitrate in bits per second.</param>
    public H264Encoder(int width, int height, int bitrate)
    {
        if (!Started.Value)
        {
            throw new InvalidOperationException("Media Foundation is not available.");
        }

        _width = width;
        _height = height;
        _nv12 = new byte[Nv12.Size(width, height)];
        _transform = (IMFTransform)(Activator.CreateInstance(Type.GetTypeFromCLSID(H264EncoderClsid, throwOnError: true)!)
            ?? throw new InvalidOperationException("The H.264 encoder is not installed."));
        try
        {
            if (_transform.GetAttributes(out var attributes) >= 0 && attributes is not null)
            {
                attributes.SetUINT32(LowLatency, 1);
                Marshal.ReleaseComObject(attributes);
            }

            // The encoder wants the output type first, then the input type.
            var output = CreateMediaType();
            output.SetGUID(MajorType, MediaTypeVideo);
            output.SetGUID(Subtype, FormatH264);
            output.SetUINT32(AverageBitrate, (uint)bitrate);
            output.SetUINT64(FrameSize, Pack(width, height));
            output.SetUINT64(FrameRate, Pack(30, 1));
            output.SetUINT64(PixelAspectRatio, Pack(1, 1));
            output.SetUINT32(InterlaceMode, InterlaceProgressive);
            output.SetUINT32(Mpeg2Profile, ProfileBaseline);
            _transform.SetOutputType(0, output, 0);
            Marshal.ReleaseComObject(output);

            var input = CreateMediaType();
            input.SetGUID(MajorType, MediaTypeVideo);
            input.SetGUID(Subtype, FormatNv12);
            input.SetUINT64(FrameSize, Pack(width, height));
            input.SetUINT64(FrameRate, Pack(30, 1));
            input.SetUINT64(PixelAspectRatio, Pack(1, 1));
            input.SetUINT32(InterlaceMode, InterlaceProgressive);
            _transform.SetInputType(0, input, 0);
            Marshal.ReleaseComObject(input);

            _transform.GetOutputStreamInfo(0, out var info);
            _outputSize = Math.Max(info.Size, (width * height * 3 / 2) + 1024);
            _transform.ProcessMessage(MessageNotifyBeginStreaming, 0);
            _transform.ProcessMessage(MessageNotifyStartOfStream, 0);
        }
        catch
        {
            Marshal.ReleaseComObject(_transform);
            throw;
        }
    }

    /// <summary>Whether this Windows has a working H.264 encoder (checked once with a tiny test frame).</summary>
    public static bool IsAvailable => Available.Value;

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// Encodes one 32-bit BGRX frame (top-down, <paramref name="stride"/> bytes per row, at least the encoder's size).
    /// Returns the Annex B access unit, or <c>null</c> if the encoder buffered the frame.
    /// </summary>
    public byte[]? Encode(ReadOnlySpan<byte> bgrx, int stride) => Encode(bgrx, stride, out _);

    /// <inheritdoc cref="Encode(ReadOnlySpan{byte}, int)"/>
    public byte[]? Encode(ReadOnlySpan<byte> bgrx, int stride, out bool keyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Nv12.FromBgrx(bgrx, stride, _width, _height, _nv12);
        var sample = CreateSampleWith(_nv12, _nv12.Length);
        try
        {
            sample.SetSampleTime(_time);
            sample.SetSampleDuration(FrameDuration);
            _time += FrameDuration;
            var result = _transform.ProcessInput(0, sample, 0);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }

        return ReadOutput(out keyframe);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Marshal.ReleaseComObject(_transform);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private static IMFMediaType CreateMediaType()
    {
        Marshal.ThrowExceptionForHR(MediaFoundationNative.CreateMediaType(out var pointer));
        try
        {
            return (IMFMediaType)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static IMFSample CreateSampleWith(byte[] data, int length)
    {
        Marshal.ThrowExceptionForHR(CreateSample(out var samplePointer));
        Marshal.ThrowExceptionForHR(CreateMemoryBuffer(length, out var bufferPointer));
        IMFSample sample;
        IMFMediaBuffer buffer;
        try
        {
            sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePointer);
            buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPointer);
        }
        finally
        {
            Marshal.Release(samplePointer);
            Marshal.Release(bufferPointer);
        }

        buffer.Lock(out var target, out _, out _);
        try
        {
            Marshal.Copy(data, 0, target, length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.SetCurrentLength(length);
        sample.AddBuffer(buffer);
        Marshal.ReleaseComObject(buffer);
        return sample;
    }

    /// <summary>An empty sample with one buffer of <paramref name="size"/> bytes for the encoder to fill.</summary>
    private static (IMFSample Sample, IMFMediaBuffer Buffer) CreateOutputSample(int size)
    {
        Marshal.ThrowExceptionForHR(CreateSample(out var samplePointer));
        Marshal.ThrowExceptionForHR(CreateMemoryBuffer(size, out var bufferPointer));
        try
        {
            var sample = (IMFSample)Marshal.GetObjectForIUnknown(samplePointer);
            var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferPointer);
            sample.AddBuffer(buffer);
            return (sample, buffer);
        }
        finally
        {
            Marshal.Release(samplePointer);
            Marshal.Release(bufferPointer);
        }
    }

    /// <summary>Collects everything the encoder has ready (normally exactly one access unit in low-latency mode).</summary>
    private byte[]? ReadOutput(out bool keyframe)
    {
        keyframe = false;
        using var collected = new MemoryStream();
        var streamChanges = 0;
        while (true)
        {
            var (sample, buffer) = CreateOutputSample(_outputSize);

            var samplePointer = Marshal.GetComInterfaceForObject(sample, typeof(IMFSample));
            try
            {
                var output = new OutputDataBuffer { StreamId = 0, Sample = samplePointer };
                var result = _transform.ProcessOutput(0, 1, ref output, out _);
                if (output.Events != 0)
                {
                    Marshal.Release(output.Events);
                }

                if (result == NeedMoreInput)
                {
                    break;
                }

                if (result == StreamChange)
                {
                    // The encoder wants a new output type: take the one it offers, then ask again (bounded).
                    if (++streamChanges > MaxStreamChanges)
                    {
                        throw new InvalidOperationException("The H.264 encoder keeps changing its output type.");
                    }

                    AcceptOfferedOutputType();
                    continue;
                }

                Marshal.ThrowExceptionForHR(result);
                if (sample.GetUINT32(CleanPoint, out var clean) >= 0 && clean != 0)
                {
                    keyframe = true;
                }

                buffer.Lock(out var data, out _, out var length);
                try
                {
                    var bytes = new byte[length];
                    Marshal.Copy(data, bytes, 0, length);
                    collected.Write(bytes);
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                Marshal.Release(samplePointer);
                Marshal.ReleaseComObject(buffer);
                Marshal.ReleaseComObject(sample);
            }
        }

        if (collected.Length == 0)
        {
            return null;
        }

        var unit = collected.ToArray();
        return keyframe && !AnnexB.HasNal(unit, 7) && SequenceHeader() is { Length: > 0 } header ? [.. header, .. unit] : unit;
    }

    private void AcceptOfferedOutputType()
    {
        Marshal.ThrowExceptionForHR(_transform.GetOutputAvailableType(0, 0, out var type));
        try
        {
            _transform.SetOutputType(0, type, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }

        _sequenceHeader = null; // belongs to the old output type
    }

    /// <summary>SPS/PPS from the output type (prepended to keyframes that do not carry them in-band).</summary>
    private byte[]? SequenceHeader()
    {
        if (_sequenceHeader is not null)
        {
            return _sequenceHeader;
        }

        _transform.GetOutputCurrentType(0, out var type);
        try
        {
            if (type.GetBlobSize(SequenceHeaderKey, out var size) >= 0 && size > 0)
            {
                var header = new byte[size];
                if (type.GetBlob(SequenceHeaderKey, header, size, out var written) >= 0)
                {
                    _sequenceHeader = header[..written];
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }

        return _sequenceHeader;
    }

    private static Guid SequenceHeaderKey => MediaFoundationNative.SequenceHeader;
}
