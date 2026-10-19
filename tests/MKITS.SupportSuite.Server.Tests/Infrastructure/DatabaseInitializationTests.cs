// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class DatabaseInitializationTests(MkitsWebApplicationFactory factory)
    : IClassFixture<MkitsWebApplicationFactory>
{
    [Fact]
    public async Task Startup_AppliesMigrations_EnablesWal_WritesServerStarted()
    {
        using var client = factory.CreateClient();
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<MkitsDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(File.Exists(Path.Combine(factory.DataDirectory, "mkits.db")));
        Assert.Equal("wal", await QueryJournalModeAsync(db), ignoreCase: true);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EventType == AuditEventTypes.ServerStarted));
    }

    private static async Task<string?> QueryJournalModeAsync(MkitsDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return (string?)await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task AuditChain_IsValidAfterStartup()
    {
        using var client = factory.CreateClient();

        var result = await factory.Services.GetRequiredService<IAuditLog>().VerifyAsync();

        Assert.True(result.IsValid);
    }
}
