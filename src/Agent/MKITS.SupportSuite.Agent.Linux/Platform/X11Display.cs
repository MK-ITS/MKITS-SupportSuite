// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Platform;

/// <summary>
/// One connection to an X server. All Xlib calls on it run under <see cref="Gate"/> (Xlib connections are not
/// thread-safe; <c>XInitThreads</c> is called once for libraries that use Xlib internally).
/// </summary>
internal sealed class X11Display : IDisposable
{
    private static readonly Lazy<int> ThreadsInitialized = new(Initialize);
    private static readonly byte[] MitMagicCookie = Encoding.ASCII.GetBytes("MIT-MAGIC-COOKIE-1");
    private static readonly Lock AuthorizationGate = new();
    private static readonly ConcurrentDictionary<nint, X11Display> Survivable = new();

    private static int _errors;

    private int _broken;

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

    /// <summary>
    /// The connection to the X server was lost (logout, X server restart). A broken display must not be used any more;
    /// open a new one. Only reported for displays opened with <see cref="OpenSurvivable"/>.
    /// </summary>
    public bool IsBroken => Volatile.Read(ref _broken) != 0;

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

    /// <summary>
    /// For the system service: opens <paramref name="name"/> with an explicit MIT-MAGIC-COOKIE-1 (the user's X server)
    /// and keeps the process alive when that X server goes away (<see cref="IsBroken"/>). <c>null</c> if the display
    /// cannot be opened, or if this libX11 cannot survive a lost connection.
    /// </summary>
    public static unsafe X11Display? OpenSurvivable(string name, byte[]? cookie)
    {
        try
        {
            _ = ThreadsInitialized.Value;
            nint handle;
            lock (AuthorizationGate)
            {
                if (cookie is not null)
                {
                    X11Native.XSetAuthorization(MitMagicCookie, MitMagicCookie.Length, cookie, cookie.Length);
                }

                try
                {
                    handle = X11Native.XOpenDisplay(name);
                }
                finally
                {
                    if (cookie is not null)
                    {
                        X11Native.XSetAuthorization(null, 0, null, 0);
                    }
                }
            }

            if (handle == 0)
            {
                return null;
            }

            var display = new X11Display(handle);
            Survivable[handle] = display;
            if (!X11Native.TrySetIoErrorExitHandler(handle, &OnIoErrorExit))
            {
                // Without the exit handler a logout would terminate the service: refuse rather than crash later.
                Survivable.TryRemove(handle, out _);
                display.Dispose();
                return null;
            }

            return display;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    private static unsafe int Initialize()
    {
        _ = X11Native.XSetErrorHandler(&OnError);
        _ = X11Native.XSetIOErrorHandler(&OnIoError);
        return X11Native.XInitThreads();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, nint errorEvent)
    {
        Interlocked.Increment(ref _errors);
        return 0;
    }

    /// <summary>Replaces the default handler's message on stderr; the exit handler decides whether the process ends.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnIoError(nint display)
    {
        if (Survivable.TryGetValue(display, out var x11))
        {
            Volatile.Write(ref x11._broken, 1);
        }

        return 0;
    }

    /// <summary>Returning (instead of calling <c>exit</c>) keeps the process alive; the display is marked broken.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIoErrorExit(nint display, nint userData)
    {
        if (Survivable.TryGetValue(display, out var x11))
        {
            Volatile.Write(ref x11._broken, 1);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            Survivable.TryRemove(Handle, out _);
            if (IsBroken)
            {
                // After an I/O error Xlib must not touch the dead connection again (XCloseDisplay would run into the
                // error handlers once more); the few kilobytes of the Display structure are left behind.
                return;
            }

            _ = X11Native.XCloseDisplay(Handle);
        }
    }
}
