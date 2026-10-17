// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Server.Infrastructure.Configuration;
using MKITS.SupportSuite.Server.Infrastructure.Health;
using MKITS.SupportSuite.Server.Infrastructure.Localization;
using MKITS.SupportSuite.Server.Infrastructure.ReverseProxy;
using MKITS.SupportSuite.Server.Infrastructure.Security;

namespace MKITS.SupportSuite.Server.Infrastructure;

/// <summary>
/// Registers and wires the cross-cutting server infrastructure.
/// Feature modules (identity, sessions, relay, …) are added separately in <c>Program.cs</c>.
/// </summary>
public static class InfrastructureExtensions
{
    public static WebApplicationBuilder AddMkitsInfrastructure(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var configuration = builder.Configuration;

        // Do not advertise the web server implementation.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

        // ---- configuration (validated at startup: misconfiguration fails fast) ----
        services.AddOptions<MkitsOptions>()
            .Bind(configuration.GetSection(MkitsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MkitsOptions>, MkitsOptionsValidator>();

        services.AddOptions<ReverseProxyOptions>()
            .Bind(configuration.GetSection(ReverseProxyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReverseProxyOptions>, ReverseProxyOptionsValidator>();
        services.ConfigureOptions<ReverseProxyConfiguration>();

        services.AddSingleton<DataDirectory>();
        services.AddSingleton(TimeProvider.System);

        // ---- Data Protection: keys persist on the data volume so cookies survive restarts ----
        // The key directory is resolved lazily from the validated options.
        services.AddDataProtection().SetApplicationName(ProductInfo.Name);
        services.AddOptions<KeyManagementOptions>()
            .Configure<DataDirectory, ILoggerFactory>((options, dataDirectory, loggerFactory) =>
                options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(dataDirectory.KeysPath), loggerFactory));

        // ---- web basics ----
        services.AddProblemDetails();
        services.AddMkitsLocalization();
        services.AddHealthChecks()
            .AddCheck<DataDirectoryHealthCheck>("data-directory");

        return builder;
    }

    /// <summary>Creates the data directory tree. Must run before the database or Data Protection is used.</summary>
    public static WebApplication PrepareDataDirectory(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Services.GetRequiredService<DataDirectory>().EnsureCreated();
        return app;
    }

    /// <summary>
    /// Must be the first middleware: every later component (IP bans, audit, rate limits) needs the real client IP and scheme.
    /// </summary>
    public static WebApplication UseMkitsForwardedHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        return app;
    }

    /// <summary>Security headers, error handling and request localization.</summary>
    public static WebApplication UseMkitsWebBasics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRequestLocalization();

        return app;
    }
}
