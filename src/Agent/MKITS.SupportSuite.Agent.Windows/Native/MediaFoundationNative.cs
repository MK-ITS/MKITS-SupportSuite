// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;

namespace MKITS.SupportSuite.Agent.Windows.Native;

// Media Foundation (mfplat.dll, mfapi.h, mfidl.h, mftransform.h): just what the H.264 software encoder MFT needs.
// Classic COM interop ([ComImport]) – every interface lists ALL vtable slots in header order; unused slots take raw
// pointers. HRESULTs are returned ([PreserveSig]) where the caller must look at them.
#pragma warning disable SYSLIB1096 // [ComImport] is used deliberately: the app is not trimmed, and vtable order is clearer here.

internal static partial class MediaFoundationNative
{
    internal const int MfVersion = 0x00020070; // MF_SDK_VERSION 0x2, MF_API_VERSION 0x70
    internal const int MfStartupLite = 1;
    internal const int NeedMoreInput = unchecked((int)0xC00D6D72); // MF_E_TRANSFORM_NEED_MORE_INPUT
    internal const int StreamChange = unchecked((int)0xC00D6D61);  // MF_E_TRANSFORM_STREAM_CHANGE
    internal const int MessageNotifyBeginStreaming = 0x10000000;
    internal const int MessageNotifyStartOfStream = 0x10000003;
    internal const uint InterlaceProgressive = 2;
    internal const uint ProfileBaseline = 66; // eAVEncH264VProfile_Base

    internal static readonly Guid H264EncoderClsid = new("6CA50344-051A-4DED-9779-A43305165E35"); // CLSID_MSH264EncoderMFT
    internal static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");     // MFMediaType_Video
    internal static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");        // MFVideoFormat_H264
    internal static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");        // MFVideoFormat_NV12
    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");         // MF_MT_MAJOR_TYPE
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");           // MF_MT_SUBTYPE
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");         // MF_MT_FRAME_SIZE
    internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");         // MF_MT_FRAME_RATE
    internal static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");  // MF_MT_PIXEL_ASPECT_RATIO
    internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");     // MF_MT_INTERLACE_MODE
    internal static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");    // MF_MT_AVG_BITRATE
    internal static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");      // MF_MT_MPEG2_PROFILE
    internal static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");    // MF_MT_MPEG_SEQUENCE_HEADER
    internal static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");        // MF_LOW_LATENCY
    internal static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");        // MFSampleExtension_CleanPoint

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutputStreamInfo
    {
        public int Flags;
        public int Size;
        public int Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutputDataBuffer
    {
        public int StreamId;
        public nint Sample;  // IMFSample*
        public int Status;
        public nint Events;  // IMFCollection*
    }

    [LibraryImport("mfplat.dll", EntryPoint = "MFStartup")]
    internal static partial int Startup(int version, int flags);

    [LibraryImport("mfplat.dll", EntryPoint = "MFCreateMediaType")]
    internal static partial int CreateMediaType(out nint mediaType);

    [LibraryImport("mfplat.dll", EntryPoint = "MFCreateSample")]
    internal static partial int CreateSample(out nint sample);

    [LibraryImport("mfplat.dll", EntryPoint = "MFCreateMemoryBuffer")]
    internal static partial int CreateMemoryBuffer(int maxLength, out nint buffer);
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    void GetItem(in Guid key, nint value);
    void GetItemType(in Guid key, out int type);
    void CompareItem(in Guid key, nint value, out int result);
    void Compare(nint other, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    void GetDouble(in Guid key, out double value);
    void GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out int length);
    void GetString(in Guid key, nint value, int size, nint length);
    void GetAllocatedString(in Guid key, out nint value, out int length);
    [PreserveSig] int GetBlobSize(in Guid key, out int size);
    [PreserveSig] int GetBlob(in Guid key, [Out] byte[] buffer, int size, out int written);
    void GetAllocatedBlob(in Guid key, out nint buffer, out int size);
    void GetUnknown(in Guid key, in Guid iid, out nint value);
    void SetItem(in Guid key, nint value);
    void DeleteItem(in Guid key);
    void DeleteAllItems();
    void SetUINT32(in Guid key, uint value);
    void SetUINT64(in Guid key, ulong value);
    void SetDouble(in Guid key, double value);
    void SetGUID(in Guid key, in Guid value);
    void SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    void SetBlob(in Guid key, [In] byte[] buffer, int size);
    void SetUnknown(in Guid key, nint value);
    void LockStore();
    void UnlockStore();
    void GetCount(out int count);
    void GetItemByIndex(int index, out Guid key, nint value);
    void CopyAllItems(nint destination);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    // IMFAttributes slots are inherited (COM interop re-declares them via "new" members below).
    new void GetItem(in Guid key, nint value);
    new void GetItemType(in Guid key, out int type);
    new void CompareItem(in Guid key, nint value, out int result);
    new void Compare(nint other, int matchType, out int result);
    [PreserveSig] new int GetUINT32(in Guid key, out uint value);
    [PreserveSig] new int GetUINT64(in Guid key, out ulong value);
    new void GetDouble(in Guid key, out double value);
    new void GetGUID(in Guid key, out Guid value);
    [PreserveSig] new int GetStringLength(in Guid key, out int length);
    new void GetString(in Guid key, nint value, int size, nint length);
    new void GetAllocatedString(in Guid key, out nint value, out int length);
    [PreserveSig] new int GetBlobSize(in Guid key, out int size);
    [PreserveSig] new int GetBlob(in Guid key, [Out] byte[] buffer, int size, out int written);
    new void GetAllocatedBlob(in Guid key, out nint buffer, out int size);
    new void GetUnknown(in Guid key, in Guid iid, out nint value);
    new void SetItem(in Guid key, nint value);
    new void DeleteItem(in Guid key);
    new void DeleteAllItems();
    new void SetUINT32(in Guid key, uint value);
    new void SetUINT64(in Guid key, ulong value);
    new void SetDouble(in Guid key, double value);
    new void SetGUID(in Guid key, in Guid value);
    new void SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    new void SetBlob(in Guid key, [In] byte[] buffer, int size);
    new void SetUnknown(in Guid key, nint value);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out int count);
    new void GetItemByIndex(int index, out Guid key, nint value);
    new void CopyAllItems(nint destination);

    void GetMajorType(out Guid type);
    void IsCompressedFormat(out int compressed);
    void IsEqual(nint other, out int flags);
    void GetRepresentation(Guid representation, out nint value);
    void FreeRepresentation(Guid representation, nint value);
}

[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample : IMFAttributes
{
    new void GetItem(in Guid key, nint value);
    new void GetItemType(in Guid key, out int type);
    new void CompareItem(in Guid key, nint value, out int result);
    new void Compare(nint other, int matchType, out int result);
    [PreserveSig] new int GetUINT32(in Guid key, out uint value);
    [PreserveSig] new int GetUINT64(in Guid key, out ulong value);
    new void GetDouble(in Guid key, out double value);
    new void GetGUID(in Guid key, out Guid value);
    [PreserveSig] new int GetStringLength(in Guid key, out int length);
    new void GetString(in Guid key, nint value, int size, nint length);
    new void GetAllocatedString(in Guid key, out nint value, out int length);
    [PreserveSig] new int GetBlobSize(in Guid key, out int size);
    [PreserveSig] new int GetBlob(in Guid key, [Out] byte[] buffer, int size, out int written);
    new void GetAllocatedBlob(in Guid key, out nint buffer, out int size);
    new void GetUnknown(in Guid key, in Guid iid, out nint value);
    new void SetItem(in Guid key, nint value);
    new void DeleteItem(in Guid key);
    new void DeleteAllItems();
    new void SetUINT32(in Guid key, uint value);
    new void SetUINT64(in Guid key, ulong value);
    new void SetDouble(in Guid key, double value);
    new void SetGUID(in Guid key, in Guid value);
    new void SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    new void SetBlob(in Guid key, [In] byte[] buffer, int size);
    new void SetUnknown(in Guid key, nint value);
    new void LockStore();
    new void UnlockStore();
    new void GetCount(out int count);
    new void GetItemByIndex(int index, out Guid key, nint value);
    new void CopyAllItems(nint destination);

    void GetSampleFlags(out int flags);
    void SetSampleFlags(int flags);
    void GetSampleTime(out long time);
    void SetSampleTime(long time);
    void GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    void GetBufferCount(out int count);
    void GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    void AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex(int index);
    void RemoveAllBuffers();
    void GetTotalLength(out int length);
    void CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    void Lock(out nint buffer, out int maxLength, out int currentLength);
    void Unlock();
    void GetCurrentLength(out int length);
    void SetCurrentLength(int length);
    void GetMaxLength(out int length);
}

[ComImport]
[Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    void GetStreamLimits(out int inputMinimum, out int inputMaximum, out int outputMinimum, out int outputMaximum);
    void GetStreamCount(out int inputs, out int outputs);
    [PreserveSig] int GetStreamIDs(int inputSize, nint inputIds, int outputSize, nint outputIds);
    void GetInputStreamInfo(int streamId, nint info);
    void GetOutputStreamInfo(int streamId, out MediaFoundationNative.OutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    void GetInputStreamAttributes(int streamId, out IMFAttributes attributes);
    void GetOutputStreamAttributes(int streamId, out IMFAttributes attributes);
    void DeleteInputStream(int streamId);
    void AddInputStreams(int count, nint ids);
    [PreserveSig] int GetInputAvailableType(int streamId, int index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(int streamId, int index, out IMFMediaType type);
    void SetInputType(int streamId, IMFMediaType? type, int flags);
    void SetOutputType(int streamId, IMFMediaType? type, int flags);
    void GetInputCurrentType(int streamId, out IMFMediaType type);
    void GetOutputCurrentType(int streamId, out IMFMediaType type);
    void GetInputStatus(int streamId, out int flags);
    void GetOutputStatus(out int flags);
    void SetOutputBounds(long lower, long upper);
    void ProcessEvent(int streamId, nint mediaEvent);
    void ProcessMessage(int message, nint parameter);
    [PreserveSig] int ProcessInput(int streamId, IMFSample sample, int flags);
    [PreserveSig] int ProcessOutput(int flags, int count, ref MediaFoundationNative.OutputDataBuffer buffers, out int status);
}
#pragma warning restore SYSLIB1096
