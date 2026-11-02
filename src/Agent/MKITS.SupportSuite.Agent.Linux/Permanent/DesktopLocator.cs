// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text.RegularExpressions;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>The graphical session of the logged-on user, as needed by the service (display, cookie) and by the process it starts there.</summary>
internal sealed record UserDesktop(LoginSession Session, UserAccount User, string Display, byte[]? Cookie, IReadOnlyDictionary<string, string> Environment);

/// <summary>Why no desktop is available (for the log).</summary>
internal enum DesktopProblem
{
    None,
    NobodyLoggedOn,
    Wayland,
    NoDisplay,
}

/// <summary>
/// Finds the logged-on user's X11 desktop: logind names the session; its processes' environment (untrusted, user
/// controlled) supplies <c>DISPLAY</c>, <c>XAUTHORITY</c> and the locale; the cookie comes from the X server's
/// <c>-auth</c> file or the user's own Xauthority – files are only read if owned by root or that user.
/// </summary>
internal static partial class DesktopLocator
{
    private const int MaxXAuthoritySize = 64 * 1024;

    /// <summary>Copied into the environment of the user-session process (never anything like <c>LD_PRELOAD</c>).</summary>
    internal static readonly string[] SessionVariables =
    [
        "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "LANG", "LANGUAGE", "LC_ALL", "LC_MESSAGES", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS",
        "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "XDG_SESSION_ID",
    ];

    private static readonly string[] XServerNames = ["Xorg", "X", "Xorg.bin"];

    /// <summary>The active local graphical session (cheap: <c>loginctl</c> only).</summary>
    public static LoginSession? FindSession() => LoginSessions.SelectGraphical(LoginSessions.Query());

    /// <summary>The session whose screen the service shows: the user's, or the X11 login screen (cheap: <c>loginctl</c> only).</summary>
    public static LoginSession? FindScreenSession() => LoginSessions.SelectScreen(LoginSessions.Query());

    public static (UserDesktop? Desktop, DesktopProblem Problem) Locate() =>
        FindSession() is { } session ? Resolve(session) : (null, DesktopProblem.NobodyLoggedOn);

    /// <summary>Display, cookie and environment of <paramref name="session"/> (scans <c>/proc</c>).</summary>
    public static (UserDesktop? Desktop, DesktopProblem Problem) Resolve(LoginSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Type == "wayland")
        {
            return (null, DesktopProblem.Wayland);
        }

        if (HostSystem.GetUser(session.Uid) is not { } user)
        {
            return (null, DesktopProblem.NoDisplay);
        }

        var environment = FindSessionEnvironment(session);
        var display = IsLocalDisplay(session.Display) ? session.Display
            : environment.GetValueOrDefault("DISPLAY") is { } fromEnvironment && IsLocalDisplay(fromEnvironment) ? fromEnvironment
            : null;
        if (display is null)
        {
            return (null, DesktopProblem.NoDisplay);
        }

        var variables = SessionEnvironment(environment, display, session.Uid);
        return (new UserDesktop(session, user, display, FindCookie(session, user, display, environment), variables), DesktopProblem.None);
    }

    /// <summary>
    /// Like <see cref="Resolve"/>, but for starting the session window: Wayland sessions are fine too – the window then
    /// runs on XWayland (if the session has it) and the screen goes through the portal in that process (ADR-0026).
    /// The service itself never connects to such a display, so no cookie is looked up.
    /// </summary>
    public static (UserDesktop? Desktop, DesktopProblem Problem) ResolveSessionWindow(LoginSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Type != "wayland")
        {
            return Resolve(session);
        }

        if (HostSystem.GetUser(session.Uid) is not { } user)
        {
            return (null, DesktopProblem.NoDisplay);
        }

        var environment = FindSessionEnvironment(session);
        if (!environment.ContainsKey("WAYLAND_DISPLAY"))
        {
            return (null, DesktopProblem.NoDisplay);
        }

        var display = environment.GetValueOrDefault("DISPLAY") is { } xwayland && IsLocalDisplay(xwayland) ? xwayland : string.Empty;
        return (new UserDesktop(session, user, display, null, SessionEnvironment(environment, display, session.Uid)), DesktopProblem.None);
    }

    /// <summary>The whitelisted variables of the session for the process started there (internal for tests).</summary>
    internal static Dictionary<string, string> SessionEnvironment(IReadOnlyDictionary<string, string> environment, string display, uint uid)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var variables = SessionVariables.Where(environment.ContainsKey).ToDictionary(k => k, k => environment[k], StringComparer.Ordinal);
        if (display.Length > 0)
        {
            variables["DISPLAY"] = display;
        }
        else
        {
            variables.Remove("DISPLAY"); // only a local display we checked
        }

        if (variables.TryGetValue("WAYLAND_DISPLAY", out var wayland) && (wayland.Contains('/', StringComparison.Ordinal) || wayland.Length > 64))
        {
            variables.Remove("WAYLAND_DISPLAY"); // a socket name in XDG_RUNTIME_DIR, never a path elsewhere
        }

        variables.TryAdd("XDG_RUNTIME_DIR", $"/run/user/{uid}");
        return variables;
    }

    /// <summary><c>:0</c> or <c>:0.0</c> – only local displays (never a TCP display a user process points to).</summary>
    internal static bool IsLocalDisplay(string? display) => display is not null && LocalDisplay().IsMatch(display);

    /// <summary>
    /// The <c>-auth</c> file of an X server for <paramref name="display"/> from its command line (e.g. GDM:
    /// <c>/usr/lib/xorg/Xorg vt2 -displayfd 3 -auth /run/user/1000/gdm/Xauthority …</c>, LightDM: <c>X :0 -auth /var/run/lightdm/root/:0</c>).
    /// </summary>
    internal static string? XServerAuthFile(IReadOnlyList<string> arguments, string display)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 || !XServerNames.Contains(Path.GetFileName(arguments[0]), StringComparer.Ordinal))
        {
            return null;
        }

        var number = XAuthority.DisplayNumber(display);
        string? authFile = null;
        var displayMatches = false;
        var usesDisplayFd = false;
        for (var i = 1; i < arguments.Count; i++)
        {
            if (arguments[i] == "-auth" && i + 1 < arguments.Count)
            {
                authFile = arguments[++i];
            }
            else if (arguments[i] == "-displayfd")
            {
                usesDisplayFd = true; // GDM: the server picks the number itself; matched via the session instead
            }
            else if (arguments[i].StartsWith(':') && XAuthority.DisplayNumber(arguments[i]) == number)
            {
                displayMatches = true;
            }
        }

        return displayMatches || usesDisplayFd ? authFile : null;
    }

    /// <summary>The environment of a process in the session that has a display (preferring one with <c>XAUTHORITY</c>).</summary>
    private static IReadOnlyDictionary<string, string> FindSessionEnvironment(LoginSession session)
    {
        IReadOnlyDictionary<string, string>? candidate = null;
        foreach (var pid in HostSystem.ProcessIds())
        {
            if (HostSystem.ProcessUid(pid) != session.Uid || HostSystem.ProcessEnvironment(pid) is not { } environment)
            {
                continue;
            }

            if (environment.GetValueOrDefault("XDG_SESSION_ID") != session.Id
                || !(environment.ContainsKey("DISPLAY") || environment.ContainsKey("WAYLAND_DISPLAY")))
            {
                continue;
            }

            if (environment.ContainsKey("XAUTHORITY"))
            {
                return environment;
            }

            candidate ??= environment;
        }

        return candidate ?? new Dictionary<string, string>();
    }

    private static byte[]? FindCookie(LoginSession session, UserAccount user, string display, IReadOnlyDictionary<string, string> environment)
    {
        // 1. The X server's own authority file: a server run by root may use a root-owned file; a rootless server
        //    (GDM) runs as the user and its file must belong to the user.
        foreach (var pid in HostSystem.ProcessIds())
        {
            if (HostSystem.ProcessArguments(pid) is not { } arguments || XServerAuthFile(arguments, display) is not { } authFile)
            {
                continue;
            }

            var serverUid = HostSystem.ProcessUid(pid);
            uint[] owners = serverUid switch
            {
                0 => [0, session.Uid],
                { } uid when uid == session.Uid => [session.Uid],
                _ => [],
            };
            if (owners.Length > 0 && Cookie(authFile, owners, display) is { } serverCookie)
            {
                return serverCookie;
            }
        }

        // 2. The user's Xauthority (from the session environment, or the default ~/.Xauthority) – user-owned only.
        uint[] userOnly = [session.Uid];
        if (environment.GetValueOrDefault("XAUTHORITY") is { Length: > 0 } path && Path.IsPathFullyQualified(path)
            && Cookie(path, userOnly, display) is { } userCookie)
        {
            return userCookie;
        }

        return Cookie(Path.Combine(user.Home, ".Xauthority"), userOnly, display);
    }

    private static byte[]? Cookie(string path, uint[] owners, string display) =>
        HostSystem.ReadOwnedFile(path, owners, MaxXAuthoritySize) is { } data && XAuthority.DisplayNumber(display) is { } number
            ? XAuthority.FindCookie(XAuthority.Parse(data), number, Environment.MachineName)
            : null;

    [GeneratedRegex(@"^:\d{1,5}(\.\d{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalDisplay();
}
