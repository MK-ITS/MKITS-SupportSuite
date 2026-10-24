// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>
/// One connection to an X server. All Xlib calls on it run under <see cref="Gate"/> (Xlib connections are not
/// thread-safe; <c>XInitThreads</c> is called once for libraries that use Xlib internally).
/// </summary>
internal sealed class X11Display : IDisposable
{
    private static readonly Lazy<int> ThreadsInitialized = new(Initialize);

    private static int _errors;

    private X11Display(nint handle)
    {
        Handle = handle;
        Root = X11Native.XDefaultRootWindow(handle);
        Screen = X11Native.XDefaultScreen(handle);
    }

    public nint Handle { get; }

    public nuint Root { get; }

    public int Screen { get; }

    public Lock Gate { get; } = new();

    /// <summary>X protocol errors so far (ignored instead of terminating the process, e.g. a capture during a resolution change).</summary>
    public static int Errors => Volatile.Read(ref _errors);

    /// <summary>Opens <paramref name="name"/> (e.g. <c>:0</c>) or <c>$DISPLAY</c>; <c>null</c> if no X server is reachable.</summary>
    public static X11Display? Open(string? name = null)
    {
        try
        {
            _ = ThreadsInitialized.Value;
            var handle = X11Native.XOpenDisplay(name);
            return handle == 0 ? null : new X11Display(handle);
        }
        catch (DllNotFoundException)
        {
            return null; // libX11 not installed (e.g. headless server)
        }
    }

    private static unsafe int Initialize()
    {
        _ = X11Native.XSetErrorHandler(&OnError);
        return X11Native.XInitThreads();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, nint errorEvent)
    {
        Interlocked.Increment(ref _errors);
        return 0;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            _ = X11Native.XCloseDisplay(Handle);
        }
    }
}
