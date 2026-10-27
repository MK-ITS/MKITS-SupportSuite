// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>Reads the latest frame: pixels (valid only during the call), width, height, stride, blue-first byte order.</summary>
internal delegate T FrameReader<T>(nint pixels, int width, int height, int stride, bool bgrx);

/// <summary>
/// Receives one PipeWire video stream (a screen shared through the portal) on PipeWire's own thread and keeps a copy
/// of the latest frame. Compositors only send frames when something changed, so the copy is also what an unchanged
/// screen looks like. Everything native is created and destroyed under the thread loop's lock.
/// </summary>
internal sealed unsafe class PipeWireCapture : IDisposable
{
    private const int MaxFrameBytes = 16384 * 16384 * 4;

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        PipeWireNative.Init(0, 0);
        return true;
    });

    private readonly Lock _frameLock = new();
    private readonly ManualResetEventSlim _firstFrame = new();
    private readonly nint _loop;
    private readonly nint _context;
    private GCHandle _self;
    private nint _core;
    private nint _stream;
    private nint _events;
    private nint _hook;
    private nint _params;
    private byte[] _frame = [];
    private int _frameStride;
    private VideoFormat? _format;
    private VideoFormat? _frameFormat;
    private long _frames;
    private int _state;
    private int _disposed;

    private PipeWireCapture()
    {
        _ = Initialized.Value;
        _loop = PipeWireNative.ThreadLoopNew("mkits-pipewire", 0);
        _context = _loop == 0 ? 0 : PipeWireNative.ContextNew(PipeWireNative.ThreadLoopGetLoop(_loop), 0, 0);
        if (_context == 0 || PipeWireNative.ThreadLoopStart(_loop) != 0)
        {
            throw new IOException("PipeWire could not be initialized.");
        }
    }

    /// <summary>Frames received so far.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>Negotiated format (after the first frames).</summary>
    public VideoFormat? Format => Volatile.Read(ref _format);

    /// <summary>The stream failed or was closed by the compositor (e.g. the user stopped sharing).</summary>
    public bool Failed => Volatile.Read(ref _state) == PipeWireNative.StateError;

    /// <summary>
    /// Connects to a stream. <paramref name="remote"/> is the portal's PipeWire connection (ownership passes to PipeWire)
    /// or <c>null</c> for the user's PipeWire daemon (tests); the stream is <paramref name="nodeId"/>, or the node named
    /// <paramref name="targetObject"/>; <paramref name="remoteName"/> selects another daemon socket (absolute path, tests).
    /// Returns <c>null</c> if libpipewire is not installed.
    /// </summary>
    public static PipeWireCapture? Connect(SafeFileHandle? remote, uint nodeId, string? targetObject = null, string? remoteName = null)
    {
        try
        {
            var capture = new PipeWireCapture();
            try
            {
                capture.ConnectStream(remote, nodeId, targetObject, remoteName);
                return capture;
            }
            catch
            {
                capture.Dispose();
                throw;
            }
        }
        catch (DllNotFoundException)
        {
            remote?.Dispose();
            return null;
        }
    }

    /// <summary>Waits until a frame has arrived (the first one can take a moment after the portal dialog).</summary>
    public bool WaitForFrame(TimeSpan timeout) => _firstFrame.Wait(timeout);

    /// <summary>Runs <paramref name="reader"/> on the latest frame; <paramref name="none"/> if there is none yet.</summary>
    public T WithLatestFrame<T>(FrameReader<T> reader, T none)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_frameLock)
        {
            if (_frameFormat is not { } format || _frame.Length == 0)
            {
                return none;
            }

            fixed (byte* pixels = _frame)
            {
                return reader((nint)pixels, format.Width, format.Height, _frameStride, format.Bgrx);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_loop != 0)
        {
            PipeWireNative.ThreadLoopStop(_loop); // no callbacks after this
            if (_stream != 0)
            {
                PipeWireNative.StreamDestroy(_stream);
            }

            if (_core != 0)
            {
                _ = PipeWireNative.CoreDisconnect(_core);
            }

            if (_context != 0)
            {
                PipeWireNative.ContextDestroy(_context);
            }

            PipeWireNative.ThreadLoopDestroy(_loop);
        }

        foreach (var block in new[] { _events, _hook, _params })
        {
            if (block != 0)
            {
                NativeMemory.Free((void*)block);
            }
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _firstFrame.Dispose();
    }

    private void ConnectStream(SafeFileHandle? remote, uint nodeId, string? targetObject, string? remoteName)
    {
        _self = GCHandle.Alloc(this);
        _events = (nint)NativeMemory.AllocZeroed(PipeWireNative.EventsSize);
        *(uint*)_events = PipeWireNative.VersionStreamEvents;
        *(delegate* unmanaged[Cdecl]<nint, int, int, nint, void>*)(_events + PipeWireNative.EventsStateChanged) = &OnStateChanged;
        *(delegate* unmanaged[Cdecl]<nint, uint, nint, void>*)(_events + PipeWireNative.EventsParamChanged) = &OnParamChanged;
        *(delegate* unmanaged[Cdecl]<nint, void>*)(_events + PipeWireNative.EventsProcess) = &OnProcess;
        _hook = (nint)NativeMemory.AllocZeroed(PipeWireNative.HookSize);

        var format = SpaPod.EnumFormat();
        _params = (nint)NativeMemory.Alloc((nuint)(format.Length + IntPtr.Size));
        var pod = _params + IntPtr.Size;
        Marshal.Copy(format, 0, pod, format.Length);
        *(nint*)_params = pod;

        PipeWireNative.ThreadLoopLock(_loop);
        try
        {
            if (remote is not null)
            {
                var fd = (int)remote.DangerousGetHandle();
                remote.SetHandleAsInvalid(); // owned by PipeWire from here on
                _core = PipeWireNative.ContextConnectFd(_context, fd, 0, 0);
            }
            else
            {
                var properties = remoteName is null ? 0 : PipeWireNative.PropertiesNewString("remote.name=" + remoteName);
                _core = PipeWireNative.ContextConnect(_context, properties, 0);
            }

            if (_core == 0)
            {
                throw new IOException("Cannot connect to PipeWire.");
            }

            var streamProperties = "media.type=Video media.category=Capture media.role=Screen"
                + (targetObject is null ? string.Empty : " target.object=" + targetObject);
            _stream = PipeWireNative.StreamNew(_core, "mkits-screen", PipeWireNative.PropertiesNewString(streamProperties));
            if (_stream == 0)
            {
                throw new IOException("Cannot create a PipeWire stream.");
            }

            PipeWireNative.StreamAddListener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));
            var result = PipeWireNative.StreamConnect(
                _stream, PipeWireNative.DirectionInput, targetObject is null ? nodeId : PipeWireNative.IdAny,
                PipeWireNative.FlagAutoconnect | PipeWireNative.FlagMapBuffers, _params, 1);
            if (result < 0)
            {
                throw new IOException($"Cannot connect the PipeWire stream ({result}).");
            }
        }
        finally
        {
            PipeWireNative.ThreadLoopUnlock(_loop);
        }
    }

    private static PipeWireCapture? From(nint data) => data == 0 ? null : GCHandle.FromIntPtr(data).Target as PipeWireCapture;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(nint data, int old, int state, nint error)
    {
        if (From(data) is { } capture)
        {
            Volatile.Write(ref capture._state, state);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnParamChanged(nint data, uint id, nint param)
    {
        if (id != SpaPod.ParamFormat || param == 0 || From(data) is not { } capture)
        {
            return;
        }

        var size = *(uint*)param;
        if (size > 64 * 1024)
        {
            return;
        }

        var format = SpaPod.ParseFormat(new ReadOnlySpan<byte>((void*)param, (int)size + 8));
        Volatile.Write(ref capture._format, format is { IsSupported: true } ? format : null);
    }

    /// <summary>A new buffer: copy the frame (shared memory only; DMA-BUF is never offered) and return the buffer.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(nint data)
    {
        if (From(data) is not { } capture || capture._stream == 0)
        {
            return;
        }

        var buffer = PipeWireNative.StreamDequeueBuffer(capture._stream);
        if (buffer == 0)
        {
            return;
        }

        try
        {
            capture.Copy(*(nint*)buffer);
        }
        finally
        {
            _ = PipeWireNative.StreamQueueBuffer(capture._stream, buffer);
        }
    }

    private void Copy(nint spaBuffer)
    {
        if (spaBuffer == 0 || *(uint*)(spaBuffer + PipeWireNative.BufferNumberOfDatas) == 0 || Volatile.Read(ref _format) is not { } format)
        {
            return;
        }

        var spaData = *(nint*)(spaBuffer + PipeWireNative.BufferDatas);
        var type = *(uint*)(spaData + PipeWireNative.DataType);
        var pointer = *(nint*)(spaData + PipeWireNative.DataPointer);
        var maxSize = *(uint*)(spaData + PipeWireNative.DataMaxSize);
        var chunk = *(nint*)(spaData + PipeWireNative.DataChunk);
        if (type is not (PipeWireNative.DataTypeMemPtr or PipeWireNative.DataTypeMemFd) || pointer == 0 || chunk == 0)
        {
            return;
        }

        var offset = *(uint*)(chunk + PipeWireNative.ChunkOffset);
        var size = *(uint*)(chunk + PipeWireNative.ChunkSize);
        var stride = *(int*)(chunk + PipeWireNative.ChunkStride);
        var bytes = (long)stride * format.Height;
        if (size == 0 || stride < format.Width * 4 || bytes > MaxFrameBytes || offset + bytes > maxSize)
        {
            return; // empty (e.g. cursor-only update) or inconsistent buffer
        }

        lock (_frameLock)
        {
            if (_frame.Length != bytes)
            {
                _frame = new byte[bytes];
            }

            new ReadOnlySpan<byte>((void*)(pointer + offset), (int)bytes).CopyTo(_frame);
            _frameStride = stride;
            _frameFormat = format;
        }

        Interlocked.Increment(ref _frames);
        _firstFrame.Set();
    }
}
