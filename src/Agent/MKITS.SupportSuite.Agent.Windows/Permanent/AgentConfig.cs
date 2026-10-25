// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text;
using System.Text.Json;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Windows.Platform;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// Configuration of the installed agent (<c>agent.json</c> in the protected data folder). The enrollment token is
/// DPAPI-protected and removed once the device is enrolled.
/// </summary>
internal sealed record AgentConfig(string ServerUri, string? ProtectedToken)
{
    private const string TokenPurpose = "agent-enrollment-token";

    /// <inheritdoc cref="ServerAddress.Parse"/>
    public static Uri? ParseServer(string? value) => ServerAddress.Parse(value);

    public static AgentConfig Create(Uri server, string? token) =>
        new(server.ToString(), string.IsNullOrEmpty(token) ? null : Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(token), TokenPurpose)));

    /// <summary>Decrypts the token (a method, not a property: it must never be serialized).</summary>
    public string? ReadToken()
    {
        if (ProtectedToken is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(ProtectedToken), TokenPurpose));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public static AgentConfig? Load(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllBytes(path));
            return config is not null && ParseServer(config.ServerUri) is not null ? config : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this));
        File.Move(temp, path, overwrite: true);
    }
}
