// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using MKITS.SupportSuite.Common.Protocol;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

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
                Assert.NotEqual("Permission_" + name, Ui.Get("Permission_" + name));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
