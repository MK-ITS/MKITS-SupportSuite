// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Server.Infrastructure.Configuration;

namespace MKITS.SupportSuite.Server.Infrastructure.Data;

/// <summary>
/// Registers the SQLite database and applies migrations at startup.
/// </summary>
public static class DatabaseSetup
{
    public static IServiceCollection AddMkitsDatabase(this IServiceCollection services)
    {
        // A factory is used by singletons (audit log, IP ban service) that need their own short-lived context;
        // request-scoped code (Identity, endpoints) gets a scoped context created by the same factory.
        services.AddDbContextFactory<MkitsDbContext>((provider, options) =>
        {
            var dataDirectory = provider.GetRequiredService<DataDirectory>();
            options.UseSqlite(BuildConnectionString(dataDirectory.DatabasePath));
        });
        services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<MkitsDbContext>>().CreateDbContext());

        return services;
    }

    /// <summary>
    /// Applies pending migrations and switches the database to WAL mode (better concurrency, safe online backups).
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var factory = app.Services.GetRequiredService<IDbContextFactory<MkitsDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
}
