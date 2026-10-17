// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class LocalizationTests(MkitsWebApplicationFactory factory)
    : IClassFixture<MkitsWebApplicationFactory>
{
    [Theory]
    [InlineData("en", "Remote support and remote maintenance")]
    [InlineData("de", "Fernwartung und Fernsupport")]
    [InlineData("de-AT", "Fernwartung und Fernsupport")]
    [InlineData("fr", "Remote support and remote maintenance")]
    public void Localizer_ReturnsTextForCulture(string culture, string expected)
    {
        var localizer = factory.Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        var previous = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);

            var text = localizer["Product_Tagline"];

            Assert.False(text.ResourceNotFound);
            Assert.Equal(expected, text.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void GermanResources_ContainEveryEnglishKey()
    {
        // Guards the "every UI text in EN and DE" rule (AGENTS.md).
        var manager = new ResourceManager("MKITS.SupportSuite.Server.Resources.SharedResource", typeof(SharedResource).Assembly);

        var english = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        var german = manager.GetResourceSet(CultureInfo.GetCultureInfo("de"), createIfNotExists: true, tryParents: false)!;

        var englishKeys = english.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet(StringComparer.Ordinal);
        var germanKeys = german.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(englishKeys);
        Assert.Empty(englishKeys.Except(germanKeys));
        Assert.Empty(germanKeys.Except(englishKeys));
    }
}
