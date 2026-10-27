// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Linux.Native;

/// <summary>libpipewire-0.3 (PipeWire ≥ 0.3.x / 1.x): just enough to receive a video stream. Struct offsets are LP64.</summary>
internal static partial class PipeWireNative
{
    private const string LibPipeWire = "libpipewire-0.3.so.0";

    internal const uint IdAny = 0xFFFFFFFF;
    internal const int DirectionInput = 0;
    internal const int FlagAutoconnect = 1 << 0;
    internal const int FlagMapBuffers = 1 << 2;
    internal const uint VersionStreamEvents = 2;

    // struct pw_stream_events: uint32 version (+4 padding), then function pointers.
    internal const int EventsSize = 96;
    internal const int EventsStateChanged = 16;
    internal const int EventsParamChanged = 40;
    internal const int EventsProcess = 64;

    internal const int HookSize = 48; // struct spa_hook

    // struct spa_buffer / spa_data / spa_chunk
    internal const int BufferNumberOfDatas = 4;
    internal const int BufferDatas = 16;
    internal const int DataType = 0;
    internal const int DataMaxSize = 20;
    internal const int DataPointer = 24;
    internal const int DataChunk = 32;
    internal const int ChunkOffset = 0;
    internal const int ChunkSize = 4;
    internal const int ChunkStride = 8;
    internal const uint DataTypeMemPtr = 1;
    internal const uint DataTypeMemFd = 2;

    internal const int StateError = -1;
    internal const int StateStreaming = 3;

    [LibraryImport(LibPipeWire, EntryPoint = "pw_init")]
    internal static partial void Init(nint argc, nint argv);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint ThreadLoopNew(string name, nint properties);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_get_loop")]
    internal static partial nint ThreadLoopGetLoop(nint loop);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_start")]
    internal static partial int ThreadLoopStart(nint loop);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_stop")]
    internal static partial void ThreadLoopStop(nint loop);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_destroy")]
    internal static partial void ThreadLoopDestroy(nint loop);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_lock")]
    internal static partial void ThreadLoopLock(nint loop);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_thread_loop_unlock")]
    internal static partial void ThreadLoopUnlock(nint loop);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_context_new")]
    internal static partial nint ContextNew(nint mainLoop, nint properties, nuint userDataSize);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_context_destroy")]
    internal static partial void ContextDestroy(nint context);

    /// <summary>Takes ownership of <paramref name="fd"/> (also on failure).</summary>
    [LibraryImport(LibPipeWire, EntryPoint = "pw_context_connect_fd")]
    internal static partial nint ContextConnectFd(nint context, int fd, nint properties, nuint userDataSize);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_context_connect")]
    internal static partial nint ContextConnect(nint context, nint properties, nuint userDataSize);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_core_disconnect")]
    internal static partial int CoreDisconnect(nint core);

    /// <summary><c>key=value key2=value2</c>.</summary>
    [LibraryImport(LibPipeWire, EntryPoint = "pw_properties_new_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint PropertiesNewString(string properties);

    /// <summary>Takes ownership of <paramref name="properties"/>.</summary>
    [LibraryImport(LibPipeWire, EntryPoint = "pw_stream_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint StreamNew(nint core, string name, nint properties);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_stream_add_listener")]
    internal static partial void StreamAddListener(nint stream, nint hook, nint events, nint data);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_stream_connect")]
    internal static partial int StreamConnect(nint stream, int direction, uint targetId, int flags, nint parameters, uint parameterCount);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_stream_dequeue_buffer")]
    internal static partial nint StreamDequeueBuffer(nint stream);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_stream_queue_buffer")]
    internal static partial int StreamQueueBuffer(nint stream, nint buffer);

    [LibraryImport(LibPipeWire, EntryPoint = "pw_stream_destroy")]
    internal static partial void StreamDestroy(nint stream);
}
