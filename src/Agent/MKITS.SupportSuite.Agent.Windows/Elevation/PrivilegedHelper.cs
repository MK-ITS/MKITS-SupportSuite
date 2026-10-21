// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Common.Protocol.V0;
using static MKITS.SupportSuite.Agent.Windows.Native.ServiceNative;

namespace MKITS.SupportSuite.Agent.Windows.Elevation;

/// <summary>
/// The privileged helper (<c>--helper &lt;id&gt;</c>): runs as SYSTEM in the customer's console session, started by the
/// temporary service. It captures and injects input on whatever desktop currently receives input – including the
/// secure desktop (UAC prompts) and the lock screen – on behalf of exactly one client process.
/// Hardening: pipe DACL allows only SYSTEM and the customer's account (no network access), only one connection is
/// accepted, the client's process id must match the one recorded by the elevated installer, and every message is
/// size-checked.
/// </summary>
internal static partial class PrivilegedHelper
{
    public static int Run(string id)
    {
        var config = ElevatedSessionConfig.Load(id);
        SasClient.CurrentId = config.Id;
        using var pipe = CreatePipe(config);

        // Wait for the client at most two minutes, then give up (the service cleans up).
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            pipe.WaitForConnectionAsync(connectTimeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return 2;
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)config.ClientProcessId)
        {
            return 3; // somebody else connected – refuse to serve
        }

        using var desktopThread = new DesktopThread();
        return ServeAsync(pipe, desktopThread).GetAwaiter().GetResult();
    }

    private static NamedPipeServerStream CreatePipe(ElevatedSessionConfig config)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(config.UserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            config.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    private static async Task<int> ServeAsync(NamedPipeServerStream pipe, DesktopThread desktop)
    {
        while (true)
        {
            byte[]? message;
            try
            {
                message = await HelperProtocol.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return 4;
            }

            if (message is null)
            {
                return 0; // client gone – the session is over
            }

            byte[]? response;
            try
            {
                response = await desktop.InvokeAsync(() => Handle(message, desktop)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException)
            {
                return 5; // malformed request
            }

            if (response == Shutdown)
            {
                return 0;
            }

            if (response is not null)
            {
                await HelperProtocol.WriteAsync(pipe, response, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static readonly byte[] Shutdown = [];

    /// <summary>Runs on the desktop thread.</summary>
    private static byte[]? Handle(byte[] message, DesktopThread desktop)
    {
        using var reader = new BinaryReader(new MemoryStream(message));
        var type = (HelperRequest)reader.ReadByte();
        desktop.FollowInputDesktop();

        switch (type)
        {
            case HelperRequest.GetMonitors:
                var monitors = desktop.Screen.GetMonitors();
                return HelperProtocol.Build(HelperRequest.GetMonitors, w =>
                {
                    w.Write(monitors.Count);
                    foreach (var m in monitors)
                    {
                        w.Write(m.Id);
                        w.Write(m.Name);
                        w.Write(m.Left);
                        w.Write(m.Top);
                        w.Write(m.Width);
                        w.Write(m.Height);
                        w.Write(m.Primary);
                    }
                });
            case HelperRequest.Capture:
                var frame = desktop.Screen.Capture(reader.ReadUInt32(), Math.Clamp(reader.ReadInt32(), 1, 100), CancellationToken.None);
                return HelperProtocol.Build(HelperRequest.Capture, w =>
                {
                    w.Write(frame is not null);
                    if (frame is not null)
                    {
                        w.Write((int)frame.Codec);
                        w.Write(frame.Width);
                        w.Write(frame.Height);
                        w.Write(frame.Data.Length);
                        w.Write(frame.Data);
                    }
                });
            case HelperRequest.MovePointer:
                desktop.Input.MovePointer(reader.ReadInt32(), reader.ReadInt32());
                return null;
            case HelperRequest.SetButton:
                desktop.Input.SetButton((PointerButtons)reader.ReadInt32(), reader.ReadBoolean());
                return null;
            case HelperRequest.Wheel:
                desktop.Input.Wheel(Math.Clamp(reader.ReadInt32(), -10, 10), Math.Clamp(reader.ReadInt32(), -10, 10));
                return null;
            case HelperRequest.Key:
                desktop.Input.Key(HelperProtocol.ReadString(reader), reader.ReadBoolean());
                return null;
            case HelperRequest.Text:
                desktop.Input.Text(HelperProtocol.ReadString(reader));
                return null;
            case HelperRequest.Special:
                var kind = (SpecialKeyKind)reader.ReadInt32();
                if (kind == SpecialKeyKind.CtrlAltDel)
                {
                    SasClient.Request(); // SendSAS only works from the service
                }
                else
                {
                    desktop.Input.Special(kind);
                }

                return null;
            case HelperRequest.Shutdown:
                return Shutdown;
            default:
                throw new InvalidDataException("Unknown request.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>
    /// One thread that owns no windows, so it may switch desktops (<c>SetThreadDesktop</c>) to follow the input desktop
    /// (Default ↔ Winlogon) before every capture or injection.
    /// </summary>
    private sealed class DesktopThread : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = [];
        private readonly Thread _thread;
        private nint _desktop;

        public DesktopThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _work.GetConsumingEnumerable())
                {
                    action();
                }
            })
            { IsBackground = true, Name = "MKITS desktop" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public GdiScreenSource Screen { get; } = new();

        public SendInputInjector Input { get; } = new();

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    result.SetResult(func());
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            });
            return result.Task;
        }

        /// <summary>Attaches the thread to the desktop that currently receives input.</summary>
        public void FollowInputDesktop()
        {
            var desktop = OpenInputDesktop(0, false, MaximumAllowed);
            if (desktop == 0)
            {
                return;
            }

            if (SetThreadDesktop(desktop))
            {
                if (_desktop != 0)
                {
                    _ = CloseDesktop(_desktop);
                }

                _desktop = desktop;
            }
            else
            {
                _ = CloseDesktop(desktop);
            }
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            Screen.Dispose();
            _work.Dispose();
        }
    }
}
