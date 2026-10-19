// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Notifications;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class NotificationTests : IDisposable
{
    private readonly MkitsWebApplicationFactory _baseFactory = new();

    public void Dispose() => _baseFactory.Dispose();

    [Fact]
    public async Task IpBanned_IsSentToEveryActiveAdmin_InTheirLanguage()
    {
        var (factory, mails) = CreateFactory(maxPerHour: 20);
        using (factory)
        {
            await CreateAdminAsync(factory, "anna", "anna@example.test", "de");
            await CreateAdminAsync(factory, "bob", "bob@example.test", "en");
            await CreateAdminAsync(factory, "carl", "carl@example.test", "en", disabled: true);

            var dispatcher = factory.Services.GetRequiredService<AdminNotificationDispatcher>();
            await dispatcher.DeliverAsync(AdminNotification.IpBanned("203.0.113.7/32", "auto:login", null), CancellationToken.None);

            Assert.Equal(2, mails.Sent.Count);
            Assert.Contains(mails.Sent, m => m.To == "anna@example.test" && m.Subject.Contains("gesperrt", StringComparison.Ordinal) && m.Body.Contains("203.0.113.7/32", StringComparison.Ordinal));
            Assert.Contains(mails.Sent, m => m.To == "bob@example.test" && m.Subject.Contains("banned", StringComparison.Ordinal));
            Assert.DoesNotContain(mails.Sent, m => m.To == "carl@example.test");
        }
    }

    [Fact]
    public async Task Delivery_IsThrottledPerHour()
    {
        var (factory, mails) = CreateFactory(maxPerHour: 1);
        using (factory)
        {
            await CreateAdminAsync(factory, "anna", "anna@example.test", "en");
            var dispatcher = factory.Services.GetRequiredService<AdminNotificationDispatcher>();

            await dispatcher.DeliverAsync(AdminNotification.IpBanned("203.0.113.7/32", "auto:login", null), CancellationToken.None);
            await dispatcher.DeliverAsync(AdminNotification.IpBanned("203.0.113.8/32", "auto:login", null), CancellationToken.None);

            Assert.Single(mails.Sent);
        }
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("smtp.example.test", "MKITS <mkits@example.test>", true)]
    [InlineData("smtp.example.test", null, false)]
    [InlineData("smtp.example.test", "not an address", false)]
    public void SmtpOptions_Validation(string? host, string? from, bool valid)
    {
        var result = new SmtpOptionsValidator().Validate(null, new SmtpOptions { Host = host, From = from });

        Assert.Equal(valid, result.Succeeded);
    }

    private (FactoryHandle factory, FakeEmailSender mails) CreateFactory(int maxPerHour)
    {
        var mails = new FakeEmailSender();
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mkits:Smtp:Host", "smtp.example.test");
            builder.UseSetting("Mkits:Smtp:From", "MKITS <mkits@example.test>");
            builder.UseSetting("Mkits:Smtp:MaxNotificationsPerHour", maxPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(mails));
        });

        return (new FactoryHandle(factory), mails);
    }

    private static async Task CreateAdminAsync(FactoryHandle factory, string name, string email, string culture, bool disabled = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var user = new MkitsUser { Id = Guid.NewGuid(), UserName = name, Email = email, DisplayName = name, UiCulture = culture, IsDisabled = disabled };
        Assert.True((await users.CreateAsync(user, "a long test passphrase")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, Roles.Admin)).Succeeded);
    }

    /// <summary>Small wrapper so the derived factory can be used with <c>using</c> and passed around.</summary>
    private sealed class FactoryHandle(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> inner) : IDisposable
    {
        public IServiceProvider Services => inner.Services;

        public void Dispose() => inner.Dispose();
    }
}
