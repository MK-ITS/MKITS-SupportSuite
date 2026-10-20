// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Resources;

namespace MKITS.SupportSuite.Agent.Windows.UI;

/// <summary>Localized UI texts (Resources/Strings.resx, Strings.de.resx) in the user's Windows display language.</summary>
internal static class UiText
{
    private static readonly ResourceManager Resources = new("MKITS.SupportSuite.Agent.Windows.Resources.Strings", typeof(UiText).Assembly);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    /// <summary>Message for a protocol error code (docs/protocol.md §7).</summary>
    public static string ForError(string? code) =>
        code is null ? Get("Status_Ended")
        : Resources.GetString("Error_" + code, CultureInfo.CurrentUICulture) ?? Format("Error_Generic", code);
}
