// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.Globalization;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>A systemd-logind session (<c>loginctl show-session</c>).</summary>
internal sealed record LoginSession(string Id, uint Uid, string UserName, string Type, string Display, bool Active, bool Remote, string Class)
{
    public bool IsLocalUserSession => Active && !Remote && Class == "user";
}

/// <summary>Finds the session of the user sitting in front of the machine (systemd-logind via <c>loginctl</c>).</summary>
internal static class LoginSessions
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>All sessions; empty if logind is not available (containers, minimal servers).</summary>
    public static IReadOnlyList<LoginSession> Query()
    {
        var list = Run("list-sessions", "--no-legend");
        if (list is null)
        {
            return [];
        }

        var sessions = new List<LoginSession>();
        foreach (var id in ParseSessionIds(list))
        {
            if (Run("show-session", id) is { } details && ParseSession(details) is { } session)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    /// <summary>The first column of <c>loginctl list-sessions --no-legend</c>; only plausible ids.</summary>
    internal static IEnumerable<string> ParseSessionIds(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .Where(id => id.Length <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));

    /// <summary>Parses the <c>Key=Value</c> lines of <c>loginctl show-session</c>.</summary>
    internal static LoginSession? ParseSession(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..].Trim();
            }
        }

        if (!values.TryGetValue("Id", out var id) || !values.TryGetValue("User", out var user)
            || !uint.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
        {
            return null;
        }

        return new LoginSession(
            id,
            uid,
            values.GetValueOrDefault("Name", string.Empty),
            values.GetValueOrDefault("Type", string.Empty),
            values.GetValueOrDefault("Display", string.Empty),
            values.GetValueOrDefault("Active") == "yes",
            values.GetValueOrDefault("Remote") == "yes",
            values.GetValueOrDefault("Class", string.Empty));
    }

    /// <summary>The active local graphical session (X11 or Wayland), preferring X11; <c>null</c> if nobody is logged on graphically.</summary>
    internal static LoginSession? SelectGraphical(IEnumerable<LoginSession> sessions) =>
        sessions.Where(s => s.IsLocalUserSession && (s.Type is "x11" or "wayland") && s.Uid != 0)
            .OrderBy(s => s.Type == "x11" ? 0 : 1)
            .FirstOrDefault();

    private static string? Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("loginctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // loginctl not installed
        }
    }
}
