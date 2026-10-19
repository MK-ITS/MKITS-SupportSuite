// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Identity error messages in the user's UI language (EN/DE) for the errors users can actually hit.</summary>
public sealed class LocalizedIdentityErrorDescriber(IStringLocalizer<SharedResource> localizer) : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError), "Identity_DefaultError");

    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), "Identity_DuplicateUserName", userName);

    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), "Identity_DuplicateEmail", email);

    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), "Identity_InvalidUserName", userName ?? string.Empty);

    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), "Identity_InvalidEmail", email ?? string.Empty);

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "Identity_PasswordMismatch");

    public override IdentityError PasswordTooShort(int length) => Error(nameof(PasswordTooShort), "Identity_PasswordTooShort", length);

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(nameof(PasswordRequiresUniqueChars), "Identity_PasswordRequiresUniqueChars", uniqueChars);

    private IdentityError Error(string code, string resourceKey, params object[] arguments) => new()
    {
        Code = code,
        Description = string.Format(CultureInfo.CurrentCulture, localizer[resourceKey].Value, arguments),
    };
}
