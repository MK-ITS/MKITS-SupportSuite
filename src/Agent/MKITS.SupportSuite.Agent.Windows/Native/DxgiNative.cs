// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Windows.Native;

/// <summary>
/// The parts of DXGI and Direct3D 11 that Desktop Duplication needs (dxgi1_2.h, d3d11.h). COM methods are called
/// through the vtable by index – the interfaces are large and only a handful of methods are used; the index comments
/// count IUnknown's three methods first. All pointers are raw COM pointers released with <see cref="Release"/>.
/// </summary>
internal static unsafe partial class DxgiNative
{
    internal const int WaitTimeout = unchecked((int)0x887A0027); // DXGI_ERROR_WAIT_TIMEOUT
    internal const int AccessLost = unchecked((int)0x887A0026); // DXGI_ERROR_ACCESS_LOST
    internal const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND

    internal const uint FormatB8G8R8A8 = 87; // DXGI_FORMAT_B8G8R8A8_UNORM, what DuplicateOutput always delivers
    internal const int RotationUnspecified = 0;
    internal const int RotationIdentity = 1;

    internal const uint UsageStaging = 3; // D3D11_USAGE_STAGING
    internal const uint CpuAccessRead = 0x20000; // D3D11_CPU_ACCESS_READ
    internal const uint MapRead = 1; // D3D11_MAP_READ
    internal const uint SdkVersion = 7; // D3D11_SDK_VERSION
    internal const int DriverTypeUnknown = 0; // required when an adapter is given

    internal static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    internal static readonly Guid IidOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    internal static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
    internal static partial int CreateFactory1(in Guid iid, out nint factory);

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    internal static partial int CreateDevice(
        nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint featureLevelCount, uint sdkVersion,
        out nint device, out int featureLevel, out nint immediateContext);

    internal static void Release(ref nint pointer)
    {
        if (pointer != 0)
        {
            ((delegate* unmanaged<nint, uint>)Vtable(pointer)[2])(pointer);
            pointer = 0;
        }
    }

    internal static int QueryInterface(nint pointer, in Guid iid, out nint result)
    {
        fixed (Guid* id = &iid)
        {
            nint found;
            var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Vtable(pointer)[0])(pointer, id, &found);
            result = hr >= 0 ? found : 0;
            return hr;
        }
    }

    /// <summary>IDXGIFactory1::EnumAdapters1 (7 IDXGIFactory methods follow IDXGIObject's four, then EnumAdapters1).</summary>
    internal static int EnumAdapters1(nint factory, uint index, out nint adapter)
    {
        nint result;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Vtable(factory)[12])(factory, index, &result);
        adapter = hr >= 0 ? result : 0;
        return hr;
    }

    /// <summary>IDXGIAdapter::EnumOutputs.</summary>
    internal static int EnumOutputs(nint adapter, uint index, out nint output)
    {
        nint result;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Vtable(adapter)[7])(adapter, index, &result);
        output = hr >= 0 ? result : 0;
        return hr;
    }

    /// <summary>IDXGIOutput::GetDesc.</summary>
    internal static int GetOutputDesc(nint output, out OutputDesc desc)
    {
        desc = default;
        fixed (OutputDesc* pointer = &desc)
        {
            return ((delegate* unmanaged<nint, OutputDesc*, int>)Vtable(output)[7])(output, pointer);
        }
    }

    /// <summary>IDXGIOutput1::DuplicateOutput (after IDXGIOutput's 12 methods and three IDXGIOutput1 methods).</summary>
    internal static int DuplicateOutput(nint output1, nint device, out nint duplication)
    {
        nint result;
        var hr = ((delegate* unmanaged<nint, nint, nint*, int>)Vtable(output1)[22])(output1, device, &result);
        duplication = hr >= 0 ? result : 0;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::GetDesc (void).</summary>
    internal static void GetDuplicationDesc(nint duplication, out DuplicationDesc desc)
    {
        desc = default;
        fixed (DuplicationDesc* pointer = &desc)
        {
            ((delegate* unmanaged<nint, DuplicationDesc*, void>)Vtable(duplication)[7])(duplication, pointer);
        }
    }

    /// <summary>IDXGIOutputDuplication::AcquireNextFrame.</summary>
    internal static int AcquireNextFrame(nint duplication, uint timeoutMilliseconds, out FrameInfo info, out nint resource)
    {
        nint result;
        info = default;
        fixed (FrameInfo* pointer = &info)
        {
            var hr = ((delegate* unmanaged<nint, uint, FrameInfo*, nint*, int>)Vtable(duplication)[8])(duplication, timeoutMilliseconds, pointer, &result);
            resource = hr >= 0 ? result : 0;
            return hr;
        }
    }

    /// <summary>IDXGIOutputDuplication::ReleaseFrame.</summary>
    internal static int ReleaseFrame(nint duplication) =>
        ((delegate* unmanaged<nint, int>)Vtable(duplication)[14])(duplication);

    /// <summary>ID3D11Device::CreateTexture2D.</summary>
    internal static int CreateTexture2D(nint device, in Texture2DDesc desc, out nint texture)
    {
        nint result;
        fixed (Texture2DDesc* pointer = &desc)
        {
            var hr = ((delegate* unmanaged<nint, Texture2DDesc*, nint, nint*, int>)Vtable(device)[5])(device, pointer, 0, &result);
            texture = hr >= 0 ? result : 0;
            return hr;
        }
    }

    /// <summary>ID3D11DeviceContext::Map (after ID3D11DeviceChild's four methods and seven drawing methods).</summary>
    internal static int Map(nint context, nint resource, out MappedSubresource mapped)
    {
        mapped = default;
        fixed (MappedSubresource* pointer = &mapped)
        {
            return ((delegate* unmanaged<nint, nint, uint, uint, uint, MappedSubresource*, int>)Vtable(context)[14])(context, resource, 0, MapRead, 0, pointer);
        }
    }

    /// <summary>ID3D11DeviceContext::Unmap.</summary>
    internal static void Unmap(nint context, nint resource) =>
        ((delegate* unmanaged<nint, nint, uint, void>)Vtable(context)[15])(context, resource, 0);

    /// <summary>ID3D11DeviceContext::CopyResource.</summary>
    internal static void CopyResource(nint context, nint destination, nint source) =>
        ((delegate* unmanaged<nint, nint, nint, void>)Vtable(context)[47])(context, destination, source);

    private static void** Vtable(nint pointer)
    {
        if (pointer == 0)
        {
            throw new ArgumentNullException(nameof(pointer));
        }

        return *(void***)pointer;
    }

    /// <summary>DXGI_OUTPUT_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;

        public readonly string Name
        {
            get
            {
                fixed (char* name = DeviceName)
                {
                    return new string(name);
                }
            }
        }
    }

    /// <summary>DXGI_OUTDUPL_DESC (DXGI_MODE_DESC inlined).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DuplicationDesc
    {
        public uint Width;
        public uint Height;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
        public int Rotation;
        public int DesktopImageInSystemMemory;
    }

    /// <summary>DXGI_OUTDUPL_FRAME_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX;
        public int PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    /// <summary>D3D11_TEXTURE2D_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    /// <summary>D3D11_MAPPED_SUBRESOURCE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
