// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Infrastructure.Data;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

/// <summary>
/// End-to-end tests of the authentication flows through the real server pipeline (forms, cookies, antiforgery).
/// Each test uses its own server instance, because setup state is global.
/// </summary>
public sealed class AuthenticationFlowTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "correct horse battery staple";

    [Fact]
    public async Task Setup_WithoutAdmin_ShowsForm_AndLoginRedirectsToSetup()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);

        using var setup = await client.GetAsync(new Uri("/setup", UriKind.Relative));
        using var login = await client.GetAsync(new Uri("/account/login", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        Assert.Contains("name=\"Input.Token\"", await setup.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/setup", HtmlForms.RedirectTarget(login));
    }

    [Fact]
    public async Task Setup_WrongToken_ShowsError_AndBansAfterFiveAttempts()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);
        const string ip = "198.51.100.7";

        for (var i = 0; i < 5; i++)
        {
            using var response = await HtmlForms.SubmitAsync(client, "/setup", "setup", SetupFields("WRONG-TOKEN"), ip);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("The setup token is invalid.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var banned = await HtmlForms.SendAsync(client, HttpMethod.Get, "/setup", ip, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, banned.StatusCode);
    }

    [Fact]
    public async Task Setup_CorrectToken_CreatesAdmin_AndSetupDisappears()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);

        using var response = await CompleteSetupAsync(factory, client);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login?setup=done", HtmlForms.RedirectTarget(response));

        using var again = await client.GetAsync(new Uri("/setup", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var admin = await users.FindByNameAsync(AdminUser);
        Assert.NotNull(admin);
        Assert.True(await users.IsInRoleAsync(admin, Roles.Admin));
    }

    [Fact]
    public async Task Login_WrongPassword_ShowsGenericError_AndIsAudited()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);
        using (await CompleteSetupAsync(factory, client))
        {
        }

        using var response = await LoginAsync(client, AdminUser, "wrong password!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid user name or password.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<MkitsDbContext>>().CreateDbContextAsync();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EventType == AuditEventTypes.LoginFailed));
    }

    [Fact]
    public async Task AdminWithoutTwoFactor_IsSentToSetup_AndAdminPagesAreDenied_UntilTotpIsEnabled()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);
        using (await CompleteSetupAsync(factory, client))
        {
        }

        // 1. Password login of an Admin without 2FA → forced to the 2FA set-up page.
        using var login = await LoginAsync(client, AdminUser, AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/account/manage/two-factor?required=true", HtmlForms.RedirectTarget(login));

        // 2. Admin pages are denied without 2FA.
        using var denied = await client.GetAsync(new Uri("/admin/users", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/account/access-denied", HtmlForms.RedirectTarget(denied), StringComparison.Ordinal);

        // 3. Enable TOTP with a code computed like an authenticator app.
        using (await client.GetAsync(new Uri("/account/manage/two-factor", UriKind.Relative)))
        {
        }

        string key;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
            key = (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync(AdminUser))!))!;
        }

        using var enable = await HtmlForms.SubmitAsync(
            client, "/account/manage/two-factor", "verify",
            new Dictionary<string, string> { ["Verify.Code"] = Totp.Compute(key, DateTimeOffset.UtcNow) });
        var enableHtml = await enable.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Contains("Recovery codes", enableHtml, StringComparison.Ordinal);

        // 4. Now the admin page is accessible.
        using var users2 = await client.GetAsync(new Uri("/admin/users", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, users2.StatusCode);
        Assert.Contains(AdminUser, await users2.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var devices = await client.GetAsync(new Uri("/admin/devices", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, devices.StatusCode);
        Assert.Contains("Permanent devices", await devices.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 5. The supporter page is accessible as well.
        using var sessions = await client.GetAsync(new Uri("/sessions", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        Assert.Contains("Support sessions", await sessions.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/admin/users")]
    [InlineData("/admin/devices")]
    [InlineData("/admin/ip-bans")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/settings")]
    [InlineData("/sessions")]
    [InlineData("/devices")]
    public async Task ProtectedPage_Anonymous_RedirectsToLogin(string path)
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", HtmlForms.RedirectTarget(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_IsRejected()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>());
        using var response = await client.PostAsync(new Uri("/account/logout", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/account/manage", "/account/manage")]
    [InlineData("//evil.example/", "/")]
    public async Task CultureSwitch_SetsCookie_AndRedirectsOnlyLocally(string returnUrl, string expectedLocation)
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(new Uri($"/culture/set?culture=de&returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, HtmlForms.RedirectTarget(response));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Culture=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HomePage_FollowsBrowserLanguage()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.AcceptLanguage.ParseAdd("de-DE,de;q=0.9");

        using var response = await client.SendAsync(request);

        Assert.Contains("Sitzungscode", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(MkitsWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Dictionary<string, string> SetupFields(string token) => new()
    {
        ["Input.Token"] = token,
        ["Input.UserName"] = AdminUser,
        ["Input.Email"] = "admin@example.test",
        ["Input.DisplayName"] = "Admin",
        ["Input.Password"] = AdminPassword,
        ["Input.ConfirmPassword"] = AdminPassword,
    };

    private static Task<HttpResponseMessage> CompleteSetupAsync(MkitsWebApplicationFactory factory, HttpClient client)
    {
        var token = factory.Services.GetRequiredService<SetupTokenService>().CurrentToken!;
        return HtmlForms.SubmitAsync(client, "/setup", "setup", SetupFields(token));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
        HtmlForms.SubmitAsync(client, "/account/login", "login", new Dictionary<string, string>
        {
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
        });
}
