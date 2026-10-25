// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Win32.SafeHandles;
using MKITS.SupportSuite.Agent.Linux.Gui;
using MKITS.SupportSuite.Agent.Linux.Native;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// <c>--agent-user</c>: the session window of the installed agent, started by the service in the user's session.
/// stdin/stdout are the bridge to the service; stdout is moved away first so that nothing else (native libraries,
/// stray console output) can write into the protocol – fd 1 then points to stderr, which the service logs.
/// </summary>
internal static class UserProcess
{
    public static int Run(string[] args)
    {
        if (Console.IsInputRedirected is false || Console.IsOutputRedirected is false)
        {
            Console.Error.WriteLine("--agent-user is started by the mkits-agent service.");
            return 2;
        }

        var bridgeOut = LibcNative.Dup(1);
        if (bridgeOut < 0 || LibcNative.Dup2(2, 1) < 0)
        {
            return 3;
        }

        var input = new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, bufferSize: 0);
        var output = new FileStream(new SafeFileHandle(bridgeOut, ownsHandle: true), FileAccess.Write, bufferSize: 0);
        using var stream = new DuplexStream(input, output);
        return App.RunBridged(stream, args);
    }
}
