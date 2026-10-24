// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using MKITS.SupportSuite.Agent.Core.Bridge;
using MKITS.SupportSuite.Agent.Windows.Platform;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// Starts the agent's session window (<c>--agent-user &lt;pipe&gt;</c>) as the user logged on at the console and connects
/// to it over a named pipe (ADR-0022). Pipe DACL: SYSTEM and that user only, network access denied, one instance; the
/// connecting process must be the one just started. The user process in turn checks that the pipe server is the
/// installed agent EXE.
/// </summary>
internal sealed class UserSessionConnector : IUserSessionConnector
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public async Task<IUserSessionConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        using var user = ConsoleUser.TryGet();
        if (user is null)
        {
            AgentLog.Write("no user logged on at the console – chat, files, clipboard and shell are not available");
            return null;
        }

        var pipeName = "MKITS-User-" + Guid.NewGuid().ToString("N");
        var pipe = CreatePipe(pipeName, user.Sid);
        var process = user.Start(AgentPaths.Executable, $"--agent-user {pipeName}");
        if (process is null)
        {
            AgentLog.Write("could not start the session window in the user's session");
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!UserSessionNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)process.Id)
            {
                throw new IOException("Unexpected process connected to the user-session pipe.");
            }

            AgentLog.Write($"session window started for {user.Sid} in session {user.SessionId}");
            return new Connection(pipe, process);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            AgentLog.Write("session window did not connect: " + ex.Message);
            await pipe.DisposeAsync().ConfigureAwait(false);
            Stop(process);
            return null;
        }
    }

    private static NamedPipeServerStream CreatePipe(string name, SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Closing the pipe ends the session window (it exits when the service disconnects).</summary>
    private sealed class Connection(NamedPipeServerStream pipe, Process process) : IUserSessionConnection
    {
        public Stream Stream => pipe;

        public async ValueTask DisposeAsync()
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            Stop(process);
        }
    }
}
