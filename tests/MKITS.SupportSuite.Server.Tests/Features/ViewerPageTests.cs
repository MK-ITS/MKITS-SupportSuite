// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Localization;
using MKITS.SupportSuite.Server.Features.Relay;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class ViewerPageTests
{
    [Fact]
    public void Render_EncodesValues_AndHasNoInlineScript()
    {
        var html = ViewerPage.Render(Guid.Parse("0192f0c4-1c2d-7a3b-8000-000000000001"), "tick\"et<>", "</script><script>alert(1)</script>", new KeyLocalizer());

        Assert.Contains("data-ticket=\"tick&quot;et&lt;&gt;\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("<script type=\"module\" src=\"/viewer/viewer.js\"></script>", html, StringComparison.Ordinal);

        // Only two script elements: the JSON data block (not executed) and the module.
        Assert.Equal(2, html.Split("<script", StringSplitOptions.None).Length - 1);
        Assert.Contains("<script type=\"application/json\" id=\"viewer-texts\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_TextsBlock_ContainsAllKeys()
    {
        var html = ViewerPage.Render(Guid.NewGuid(), "t", "Sam", new KeyLocalizer());

        Assert.All(ViewerPage.TextKeys, key => Assert.Contains($"\"{key}\"", html, StringComparison.Ordinal));
    }

    [Fact]
    public void Render_DeviceKey_OnlyForPermanentSessions()
    {
        Assert.DoesNotContain("data-device-key", ViewerPage.Render(Guid.NewGuid(), "t", "Sam", new KeyLocalizer()), StringComparison.Ordinal);

        var html = ViewerPage.Render(Guid.NewGuid(), "t", "Sam", new KeyLocalizer(), [1, 2, 251]);
        Assert.Contains("data-device-key=\"AQL7\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentSecurityPolicy_KeepsScriptsSameOrigin()
    {
        var directives = ViewerPage.ContentSecurityPolicy.Split(';', StringSplitOptions.TrimEntries);

        Assert.Contains("script-src 'self'", directives);
        Assert.Contains("object-src 'none'", directives);
        Assert.Contains("frame-ancestors 'none'", directives);
    }

    [Fact]
    public async Task Anonymous_IsRedirectedToLogin()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri($"/sessions/{Guid.NewGuid()}/view", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", HtmlForms.RedirectTarget(response), StringComparison.Ordinal);
    }

    private sealed class KeyLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
