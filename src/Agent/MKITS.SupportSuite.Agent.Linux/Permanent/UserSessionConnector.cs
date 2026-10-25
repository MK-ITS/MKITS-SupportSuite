// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using MKITS.SupportSuite.Agent.Core.Bridge;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// Starts the agent's session window (<c>--agent-user</c>) as the user logged on graphically (<c>runuser</c>, so
/// the process has exactly that user's rights) and talks to it over its stdin/stdout (ADR-0025). Only the started
/// process holds these pipes – no socket another process could connect to. Its environment is built from scratch:
/// the display, Xauthority and locale of the user's session, nothing else.
/// </summary>
internal sealed class UserSessionConnector(
    Func<LoginSession?> findSession,
    Func<LoginSession, (UserDesktop? Desktop, DesktopProblem Problem)> resolve,
    string executable) : IUserSessionConnector
{
    private const string RunUser = "/usr/sbin/runuser";

    public UserSessionConnector()
        : this(DesktopLocator.FindSession, DesktopLocator.Resolve, AgentPaths.Executable)
    {
    }

    public Task<IUserSessionConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        if (findSession() is not { } session)
        {
            AgentLog.Write("nobody is logged on graphically – chat, files and clipboard are not available");
            return Task.FromResult<IUserSessionConnection?>(null);
        }

        var (desktop, problem) = resolve(session);
        if (desktop is null)
        {
            AgentLog.Write($"no session window for {session.UserName}: {problem}");
            return Task.FromResult<IUserSessionConnection?>(null);
        }

        var start = CreateStartInfo(desktop, executable);
        try
        {
            var process = Process.Start(start);
            if (process is null)
            {
                return Task.FromResult<IUserSessionConnection?>(null);
            }

            _ = ForwardErrorsAsync(process);
            AgentLog.Write($"session window started for {desktop.User.Name} on {desktop.Display}");
            var stream = new DuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            return Task.FromResult<IUserSessionConnection?>(new Connection(process, stream));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AgentLog.Write("could not start the session window: " + ex.Message);
            return Task.FromResult<IUserSessionConnection?>(null);
        }
    }

    /// <summary><c>runuser -u &lt;user&gt; -- &lt;agent&gt; --agent-user</c> with a minimal environment (internal for tests).</summary>
    internal static ProcessStartInfo CreateStartInfo(UserDesktop desktop, string executable)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var start = new ProcessStartInfo(RunUser)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(desktop.User.Home) ? desktop.User.Home : "/",
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(desktop.User.Name);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(executable);
        start.ArgumentList.Add("--agent-user");

        start.Environment.Clear();
        foreach (var (key, value) in desktop.Environment)
        {
            start.Environment[key] = value;
        }

        start.Environment["HOME"] = desktop.User.Home;
        start.Environment["USER"] = desktop.User.Name;
        start.Environment["LOGNAME"] = desktop.User.Name;
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        return start;
    }

    private static async Task ForwardErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                AgentLog.Write("session window: " + (line.Length > 300 ? line[..300] : line));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // process gone
        }
    }

    /// <summary>Disposing closes the pipes (the window ends with the session) and stops the process if it lingers.</summary>
    private sealed class Connection(Process process, Stream stream) : IUserSessionConnection
    {
        public Stream Stream => stream;

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
