// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Resources;

namespace MKITS.SupportSuite.Agent.Linux.Terminal;

/// <summary>Localized texts (Resources/Strings.resx, Strings.de.resx) in the language of <c>LANG</c>.</summary>
internal static class Text
{
    private static readonly ResourceManager Resources = new("MKITS.SupportSuite.Agent.Linux.Resources.Strings", typeof(Text).Assembly);

    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static string ForError(string code) =>
        Resources.GetString("Error_" + code, CultureInfo.CurrentUICulture) ?? Format("Error_Generic", code);
}
