// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Diagnostics.HealthChecks;
using MKITS.SupportSuite.Server.Infrastructure.Configuration;

namespace MKITS.SupportSuite.Server.Infrastructure.Health;

/// <summary>
/// Reports unhealthy if the data directory is missing or not writable (e.g. volume not mounted, wrong permissions).
/// </summary>
public sealed class DataDirectoryHealthCheck(DataDirectory dataDirectory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = Path.Combine(dataDirectory.RootPath, $".health-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The path is not included in the response: /health is public.
            return HealthCheckResult.Unhealthy("Data directory is not writable.", ex);
        }
    }
}
