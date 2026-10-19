// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.Identity;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class UserAdministrationTests : IAsyncLifetime, IDisposable
{
    private readonly MkitsWebApplicationFactory _factory = new();
    private AsyncServiceScope _scope;
    private UserManager<MkitsUser> _users = default!;
    private UserAdministration _admin = default!;
    private Guid _adminId;

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _users = _scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        _admin = _scope.ServiceProvider.GetRequiredService<UserAdministration>();

        var admin = new MkitsUser { Id = Guid.NewGuid(), UserName = "root", Email = "root@example.test", DisplayName = "Root" };
        Assert.True((await _users.CreateAsync(admin, "a long admin passphrase")).Succeeded);
        Assert.True((await _users.AddToRoleAsync(admin, Roles.Admin)).Succeeded);
        _adminId = admin.Id;
    }

    public async Task DisposeAsync() => await _scope.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_GeneratesWorkingInitialPassword_AndRole()
    {
        var result = await _admin.CreateAsync(_adminId, "anna", "anna@example.test", "Anna", Roles.User, "de");

        Assert.True(result.Succeeded);
        var anna = await _users.FindByNameAsync("anna");
        Assert.NotNull(anna);
        Assert.Equal("de", anna.UiCulture);
        Assert.True(await _users.CheckPasswordAsync(anna, result.Secret!));
        Assert.True(await _users.IsInRoleAsync(anna, Roles.User));
    }

    [Fact]
    public async Task Create_DuplicateUserName_FailsWithMessage()
    {
        var result = await _admin.CreateAsync(_adminId, "root", "other@example.test", "x", Roles.User, null);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task SelfActions_AreRefused()
    {
        Assert.Equal(new[] { UserAdministration.ErrorSelf }, (await _admin.SetDisabledAsync(_adminId, _adminId, true)).Errors);
        Assert.Equal(new[] { UserAdministration.ErrorSelf }, (await _admin.SetRoleAsync(_adminId, _adminId, Roles.User)).Errors);
        Assert.Equal(new[] { UserAdministration.ErrorSelf }, (await _admin.ResetTwoFactorAsync(_adminId, _adminId)).Errors);
        Assert.Equal(new[] { UserAdministration.ErrorSelf }, (await _admin.ResetPasswordAsync(_adminId, _adminId)).Errors);
    }

    [Fact]
    public async Task LastActiveAdmin_CannotBeDemotedOrDisabled_ByAnotherAdmin()
    {
        // A second admin who is disabled does not count as "active".
        var other = await CreateUserAsync("helper", Roles.Admin);
        Assert.True((await _admin.SetDisabledAsync(_adminId, other.Id, true)).Succeeded);

        var actorId = other.Id; // pretend the disabled helper tries to act on root
        Assert.Equal(new[] { UserAdministration.ErrorLastAdmin }, (await _admin.SetDisabledAsync(actorId, _adminId, true)).Errors);
        Assert.Equal(new[] { UserAdministration.ErrorLastAdmin }, (await _admin.SetRoleAsync(actorId, _adminId, Roles.User)).Errors);
    }

    [Fact]
    public async Task Disable_BlocksSignIn()
    {
        var anna = await CreateUserAsync("anna2", Roles.User);
        var signIn = _scope.ServiceProvider.GetRequiredService<SignInManager<MkitsUser>>();

        Assert.True((await _admin.SetDisabledAsync(_adminId, anna.Id, true)).Succeeded);

        var reloaded = (await _users.FindByIdAsync(anna.Id.ToString()))!;
        Assert.True(reloaded.IsDisabled);
        Assert.False(await signIn.CanSignInAsync(reloaded));
    }

    [Fact]
    public async Task ResetPassword_ReturnsNewWorkingPassword()
    {
        var anna = await CreateUserAsync("anna3", Roles.User);

        var result = await _admin.ResetPasswordAsync(_adminId, anna.Id);

        Assert.True(result.Succeeded);
        Assert.True(await _users.CheckPasswordAsync((await _users.FindByIdAsync(anna.Id.ToString()))!, result.Secret!));
    }

    [Fact]
    public async Task List_ContainsRolesAndStatus()
    {
        await CreateUserAsync("bob", Roles.User);

        var list = await _admin.ListAsync();

        Assert.Contains(list, u => u.UserName == "root" && u.Role == Roles.Admin);
        Assert.Contains(list, u => u.UserName == "bob" && u.Role == Roles.User && !u.IsDisabled);
    }

    private async Task<MkitsUser> CreateUserAsync(string name, string role)
    {
        var result = await _admin.CreateAsync(_adminId, name, $"{name}@example.test", name, role, null);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors));
        return (await _users.FindByNameAsync(name))!;
    }
}
