// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Tests.TestSupport;

/// <summary>
/// A real SQLite database in a temporary file, migrated like production. Deleted on dispose.
/// </summary>
public sealed class TestDatabase : IDbContextFactory<MkitsDbContext>, IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mkits-test-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<MkitsDbContext> _options;

    public TestDatabase()
    {
        _options = new DbContextOptionsBuilder<MkitsDbContext>()
            .UseSqlite(DatabaseSetup.BuildConnectionString(_path))
            .Options;

        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public MkitsDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
