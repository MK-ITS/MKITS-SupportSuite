// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace MKITS.SupportSuite.Agent.Core.Updates;

/// <summary>What the server returns for <c>GET /api/agent-updates/{platform}</c>: manifest and signature, base64.</summary>
public sealed record UpdateOffer(
    [property: JsonPropertyName("manifest")] string Manifest,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>
/// Asks the server for a newer agent build and downloads it (ADR-0023). Trust comes only from the release key compiled
/// into this agent – never from the server: a manifest that is not signed by that key, is for another platform or is
/// not newer than <c>currentBuild</c> is ignored, and the file must match the signed SHA-256 and size.
/// </summary>
public sealed class AgentUpdateClient(HttpClient http, Uri serverUri, string platform, long currentBuild, byte[]? trustedKey)
{
    /// <summary>Whether this build can update itself at all (release key present, not a local build).</summary>
    public bool Enabled => trustedKey is not null && currentBuild > 0;

    /// <summary>A verified, newer manifest, or <c>null</c>.</summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        using var response = await http.GetAsync(new Uri(serverUri, $"api/agent-updates/{platform}"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null; // 404: the server has no signed build for this platform
        }

        var offer = await response.Content.ReadFromJsonAsync<UpdateOffer>(cancellationToken).ConfigureAwait(false);
        if (offer is null)
        {
            return null;
        }

        byte[] manifestBytes;
        byte[] signature;
        try
        {
            manifestBytes = Convert.FromBase64String(offer.Manifest);
            signature = Convert.FromBase64String(offer.Signature);
        }
        catch (FormatException)
        {
            return null;
        }

        var manifest = UpdateManifest.Verify(manifestBytes, signature, trustedKey);
        return manifest is not null && manifest.Platform == platform && manifest.Build > currentBuild ? manifest : null;
    }

    /// <summary>
    /// Downloads the file to <paramref name="targetPath"/> (via a temporary file next to it). Returns <c>false</c> – and
    /// leaves nothing behind – if the size or the SHA-256 does not match the signed manifest.
    /// </summary>
    public async Task<bool> DownloadAsync(UpdateManifest manifest, string targetPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var temp = targetPath + ".download";
        try
        {
            using var response = await http.GetAsync(
                new Uri(serverUri, $"api/agent-updates/{platform}/file"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength is { } length && length != manifest.Size))
            {
                return false;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > manifest.Size)
                    {
                        return false;
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (total != manifest.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != manifest.Sha256)
            {
                return false;
            }

            File.Move(temp, targetPath, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
