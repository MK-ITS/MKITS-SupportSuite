// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using System.Text;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// First-run protection (docs/security-concept.md §4): while no Admin exists, a one-time setup token is generated
/// and written to the log. Only someone with access to the server logs can create the first Admin via <c>/setup</c>.
/// </summary>
public sealed partial class SetupTokenService(ILogger<SetupTokenService> logger) : IDisposable
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I
    private const int GroupCount = 4;
    private const int GroupLength = 5;

    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private volatile string? _token;

    /// <summary><c>true</c> while no Admin exists and the setup page is available.</summary>
    public bool IsSetupRequired => _token is not null;

    /// <summary>The current token – for tests only.</summary>
    internal string? CurrentToken => _token;

    /// <summary>Generates a new token and logs it. Called at startup when no Admin exists.</summary>
    public void Activate()
    {
        var token = string.Join('-', Enumerable.Range(0, GroupCount).Select(_ => RandomNumberGenerator.GetString(Alphabet, GroupLength)));
        _token = token;

        // Intentional exception to "never log secrets": this is the only channel through which the operator
        // receives the token. It is single-use and invalid as soon as the first Admin exists.
        LogSetupToken(logger, token);
    }

    /// <summary>
    /// Runs <paramref name="createAdmin"/> if <paramref name="candidate"/> matches the token. Serialized, so two
    /// simultaneous requests cannot both create an Admin. The token is consumed only if creation succeeds.
    /// </summary>
    /// <returns><c>null</c> if the token was wrong (or setup is no longer required), otherwise the result of <paramref name="createAdmin"/>.</returns>
    public async Task<bool?> RunSetupAsync(string? candidate, Func<Task<bool>> createAdmin)
    {
        ArgumentNullException.ThrowIfNull(createAdmin);

        await _setupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var token = _token;
            if (token is null || candidate is null || !FixedTimeEquals(Normalize(candidate), Normalize(token)))
            {
                return null;
            }

            var created = await createAdmin().ConfigureAwait(false);
            if (created)
            {
                _token = null;
            }

            return created;
        }
        finally
        {
            _setupLock.Release();
        }
    }

    public void Dispose() => _setupLock.Dispose();

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    [LoggerMessage(Level = LogLevel.Warning, Message = "No administrator exists yet. Open /setup and enter this one-time setup token: {SetupToken}")]
    private static partial void LogSetupToken(ILogger logger, string setupToken);
}
