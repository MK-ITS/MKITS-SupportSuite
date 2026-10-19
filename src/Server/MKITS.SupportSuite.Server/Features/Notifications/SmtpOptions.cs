// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.Notifications;

/// <summary>How the SMTP connection is secured.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (usually port 587). Default.</summary>
    StartTls = 0,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    SslOnConnect = 1,

    /// <summary>No encryption – only for a relay on localhost or inside a trusted network.</summary>
    None = 2,
}

/// <summary>
/// E-mail settings, bound from <c>Mkits:Smtp</c>. Leave <see cref="Host"/> empty to disable e-mail.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Mkits:Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? UserName { get; set; }

    /// <summary>Never logged or shown in the UI.</summary>
    public string? Password { get; set; }

    /// <summary>Sender, e.g. <c>MKITS Support &lt;mkits@example.com&gt;</c>.</summary>
    public string? From { get; set; }

    /// <summary>Upper limit of notification e-mails per hour (protection against mail floods during attacks).</summary>
    public int MaxNotificationsPerHour { get; set; } = 20;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Validates <see cref="SmtpOptions"/> at startup (only when e-mail is configured).</summary>
public sealed class SmtpOptionsValidator : IValidateOptions<SmtpOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"{SmtpOptions.SectionName}:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.From) || !MailAddress.TryCreate(options.From, out _))
        {
            failures.Add($"{SmtpOptions.SectionName}:From must be a valid sender address when Host is set, e.g. \"MKITS <mkits@example.com>\".");
        }

        if (options.MaxNotificationsPerHour < 1)
        {
            failures.Add($"{SmtpOptions.SectionName}:MaxNotificationsPerHour must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
