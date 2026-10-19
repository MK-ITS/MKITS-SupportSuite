// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Common.Sessions;

namespace MKITS.SupportSuite.Server.Features.Sessions;

/// <summary>Session settings (<c>Mkits:Sessions</c>, docs/session-lifecycle.md). All durations are TimeSpans ("00:10:00").</summary>
public sealed class SessionOptions
{
    public const string SectionName = "Mkits:Sessions";

    /// <summary>Number of digits of ad-hoc codes (6–12).</summary>
    public int CodeLength { get; set; } = SessionCodeFormat.DefaultLength;

    /// <summary>How long a code can be redeemed by an agent.</summary>
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Upper limit for codes that are valid at the same time (server-wide). Keeps guessing impractical.</summary>
    public int MaxActiveCodes { get; set; } = 50;

    /// <summary>Upper limit of open codes per supporter.</summary>
    public int MaxActiveCodesPerUser { get; set; } = 10;

    /// <summary>A connected agent without any viewer is disconnected after this time.</summary>
    public TimeSpan ViewerWaitTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the customer has to answer a consent request.</summary>
    public TimeSpan ConsentTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Reconnect window after a peer lost its connection.</summary>
    public TimeSpan SuspendGracePeriod { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Hard upper limit of a session.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Lifetime of a one-time viewer ticket.</summary>
    public TimeSpan ViewerTicketLifetime { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Rejects nonsensical session settings at startup.</summary>
public sealed class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
    public ValidateOptionsResult Validate(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        if (options.CodeLength is < SessionCodeFormat.MinLength or > SessionCodeFormat.MaxLength)
        {
            errors.Add($"{SessionOptions.SectionName}:CodeLength must be between {SessionCodeFormat.MinLength} and {SessionCodeFormat.MaxLength}.");
        }

        RequireRange(errors, nameof(options.CodeLifetime), options.CodeLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
        RequireRange(errors, nameof(options.ViewerWaitTimeout), options.ViewerWaitTimeout, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
        RequireRange(errors, nameof(options.ConsentTimeout), options.ConsentTimeout, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
        RequireRange(errors, nameof(options.SuspendGracePeriod), options.SuspendGracePeriod, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));
        RequireRange(errors, nameof(options.MaxDuration), options.MaxDuration, TimeSpan.FromMinutes(5), TimeSpan.FromDays(7));
        RequireRange(errors, nameof(options.ViewerTicketLifetime), options.ViewerTicketLifetime, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));

        if (options.MaxActiveCodes is < 1 or > 10_000)
        {
            errors.Add($"{SessionOptions.SectionName}:MaxActiveCodes must be between 1 and 10000.");
        }

        if (options.MaxActiveCodesPerUser < 1 || options.MaxActiveCodesPerUser > options.MaxActiveCodes)
        {
            errors.Add($"{SessionOptions.SectionName}:MaxActiveCodesPerUser must be between 1 and MaxActiveCodes.");
        }

        // Guessing budget: active codes must stay a tiny fraction of the code space (8 digits: up to 1000 codes).
        if (options.CodeLength is >= SessionCodeFormat.MinLength and <= SessionCodeFormat.MaxLength
            && options.MaxActiveCodes > Math.Pow(10, options.CodeLength) / 100_000)
        {
            errors.Add($"{SessionOptions.SectionName}:MaxActiveCodes is too high for CodeLength {options.CodeLength} (at most one in 100,000 codes may be valid at the same time).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void RequireRange(List<string> errors, string name, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SessionOptions.SectionName}:{name} must be between {min} and {max}.");
        }
    }
}
