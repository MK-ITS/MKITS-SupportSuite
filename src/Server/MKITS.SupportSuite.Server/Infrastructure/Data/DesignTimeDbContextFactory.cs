// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MKITS.SupportSuite.Server.Infrastructure.Data;

/// <summary>
/// Used only by the EF Core tools (<c>dotnet ef migrations add</c>) so they do not have to start the web host.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MkitsDbContext>
{
    public MkitsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MkitsDbContext>()
            .UseSqlite(DatabaseSetup.BuildConnectionString(Path.Combine(Path.GetTempPath(), "mkits-design-time.db")))
            .Options;

        return new MkitsDbContext(options);
    }
}
