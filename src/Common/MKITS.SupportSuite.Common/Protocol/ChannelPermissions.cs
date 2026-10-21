// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Common.Protocol;

/// <summary>What a viewer may do in a session. Bit values equal the server's device-group permissions.</summary>
[Flags]
public enum ViewerPermissions
{
    None = 0,
    View = 1,
    Control = 2,
    Files = 4,
    Shell = 8,
    Clipboard = 16,
    All = View | Control | Files | Shell | Clipboard,
}

/// <summary>
/// Which permission a channel needs (docs/protocol.md §5). Enforced by the relay and again by the agent, in both
/// directions. Unknown channels need all permissions, so a newer peer can never open a side door.
/// </summary>
public static class ChannelPermissions
{
    private static readonly (ViewerPermissions Flag, string Name)[] Names =
    [
        (ViewerPermissions.View, "view"),
        (ViewerPermissions.Control, "control"),
        (ViewerPermissions.Files, "files"),
        (ViewerPermissions.Shell, "shell"),
        (ViewerPermissions.Clipboard, "clipboard"),
    ];

    public static ViewerPermissions Required(uint channel) => (Channel)channel switch
    {
        Channel.Video or Channel.Display or Channel.Chat => ViewerPermissions.View,
        Channel.Input => ViewerPermissions.Control,
        Channel.Clipboard => ViewerPermissions.Clipboard,
        Channel.File => ViewerPermissions.Files,
        Channel.Shell => ViewerPermissions.Shell,
        _ => ViewerPermissions.All,
    };

    /// <summary>Wire names, e.g. <c>["view", "control"]</c> (<c>ViewerJoined.permissions</c>, <c>ConsentRequest.requested_permissions</c>).</summary>
    public static IReadOnlyList<string> ToNames(ViewerPermissions permissions) =>
        Names.Where(n => permissions.HasFlag(n.Flag)).Select(n => n.Name).ToList();

    /// <summary>Parses wire names; unknown names are ignored.</summary>
    public static ViewerPermissions Parse(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var result = ViewerPermissions.None;
        foreach (var name in names)
        {
            foreach (var (flag, wireName) in Names)
            {
                if (string.Equals(name, wireName, StringComparison.Ordinal))
                {
                    result |= flag;
                }
            }
        }

        return result;
    }
}
