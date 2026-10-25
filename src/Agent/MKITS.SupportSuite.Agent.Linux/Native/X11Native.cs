// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Linux.Native;

/// <summary>Xlib, XTest and XRandR (libX11.so.6, libXtst.so.6, libXrandr.so.2). Only what the agent needs.</summary>
internal static partial class X11Native
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";
    private const string LibXrandr = "libXrandr.so.2";

    internal const int ZPixmap = 2;
    internal const ulong AllPlanes = ulong.MaxValue;

    /// <summary>Prefix of <c>XImage</c> (LP64 layout, x86_64 and arm64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public nint Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public nuint RedMask;
        public nuint GreenMask;
        public nuint BlueMask;
    }

    /// <summary><c>XRRMonitorInfo</c> (LP64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XRRMonitorInfo
    {
        public nuint Name;
        public int Primary;
        public int Automatic;
        public int OutputCount;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int MillimeterWidth;
        public int MillimeterHeight;
        public nint Outputs;
    }

    /// <summary>Prefix of <c>XWindowAttributes</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XWindowAttributesPrefix
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
    }

    [LibraryImport(LibX11)]
    internal static partial int XInitThreads();

    /// <summary>Replaces the default handler, which would terminate the process on any protocol error.</summary>
    [LibraryImport(LibX11)]
    internal static unsafe partial nint XSetErrorHandler(delegate* unmanaged[Cdecl]<nint, nint, int> handler);

    /// <summary>
    /// Called when the connection to the X server is lost (e.g. the user logged out). Xlib then calls the display's
    /// exit handler, which terminates the process unless replaced (<see cref="TrySetIoErrorExitHandler"/>).
    /// </summary>
    [LibraryImport(LibX11)]
    internal static unsafe partial nint XSetIOErrorHandler(delegate* unmanaged[Cdecl]<nint, int> handler);

    /// <summary>
    /// Authorization for the following <see cref="XOpenDisplay"/> calls instead of <c>$XAUTHORITY</c> (the system service
    /// uses the cookie of the user's X server). <c>null</c>/0 restores the default lookup.
    /// </summary>
    [LibraryImport(LibX11)]
    internal static partial void XSetAuthorization(byte[]? name, int nameLength, byte[]? data, int dataLength);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint XOpenDisplay(string? name);

    /// <summary>
    /// <c>XSetIOErrorExitHandler</c> (libX11 ≥ 1.7): a handler that returns instead of exiting, so a lost X server does
    /// not kill a long-running process. Returns <c>false</c> on older libX11 (then the process exits and systemd restarts it).
    /// </summary>
    internal static unsafe bool TrySetIoErrorExitHandler(nint display, delegate* unmanaged[Cdecl]<nint, nint, void> handler)
    {
        if (!NativeLibrary.TryLoad(LibX11, out var library) || !NativeLibrary.TryGetExport(library, "XSetIOErrorExitHandler", out var export))
        {
            return false;
        }

        ((delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, nint, void>, nint, void>)export)(display, handler, 0);
        return true;
    }

    [LibraryImport(LibX11)]
    internal static partial int XCloseDisplay(nint display);

    [LibraryImport(LibX11)]
    internal static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11)]
    internal static partial int XDisplayWidth(nint display, int screen);

    [LibraryImport(LibX11)]
    internal static partial int XDisplayHeight(nint display, int screen);

    [LibraryImport(LibX11)]
    internal static partial int XDefaultScreen(nint display);

    [LibraryImport(LibX11)]
    internal static partial nint XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, ulong planeMask, int format);

    [LibraryImport(LibX11)]
    internal static partial int XDestroyImage(nint image);

    [LibraryImport(LibX11)]
    internal static partial int XFlush(nint display);

    [LibraryImport(LibX11)]
    internal static partial int XSync(nint display, int discard);

    [LibraryImport(LibX11)]
    internal static partial int XFree(nint data);

    [LibraryImport(LibX11)]
    internal static partial nint XGetAtomName(nint display, nuint atom);

    [LibraryImport(LibX11)]
    internal static partial byte XKeysymToKeycode(nint display, nuint keysym);

    [LibraryImport(LibX11)]
    internal static partial int XDisplayKeycodes(nint display, out int minKeycode, out int maxKeycode);

    [LibraryImport(LibX11)]
    internal static partial nint XGetKeyboardMapping(nint display, byte firstKeycode, int count, out int keysymsPerKeycode);

    [LibraryImport(LibX11)]
    internal static partial int XChangeKeyboardMapping(nint display, int firstKeycode, int keysymsPerKeycode, nuint[] keysyms, int count);

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XQueryPointer(
        nint display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport(LibXtst)]
    internal static partial int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);

    [LibraryImport(LibXtst)]
    internal static partial int XTestFakeButtonEvent(nint display, uint button, [MarshalAs(UnmanagedType.Bool)] bool isPress, nuint delay);

    [LibraryImport(LibXtst)]
    internal static partial int XTestFakeKeyEvent(nint display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool isPress, nuint delay);

    [LibraryImport(LibXrandr)]
    internal static partial nint XRRGetMonitors(nint display, nuint window, [MarshalAs(UnmanagedType.Bool)] bool getActive, out int count);

    [LibraryImport(LibXrandr)]
    internal static partial void XRRFreeMonitors(nint monitors);
}
