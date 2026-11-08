// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Agent.Windows.UI;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

/// <summary>Consent prompts list the supporter's permissions by name: every permission needs a text in both languages.</summary>
public sealed class PermissionTextTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void EveryPermission_HasAText(string language)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            foreach (var name in ChannelPermissions.ToNames(ViewerPermissions.All))
            {
                Assert.NotEqual("Permission_" + name, UiText.Get("Permission_" + name));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
