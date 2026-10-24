// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// A small text log in the protected data folder (for administrators diagnosing a device). Rotates at 1 MiB, keeps
/// one old file. Never log secrets (tokens, keys, session payloads).
/// </summary>
internal static class AgentLog
{
    private const long MaxSize = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string? Path { get; set; }

    public static void Write(string message)
    {
        if (Path is not { } path)
        {
            return;
        }

        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}{Environment.NewLine}");
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // logging must never break the agent
            }
        }
    }
}
