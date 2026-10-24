// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MKITS.SupportSuite.Agent.Core.Updates;

/// <summary>
/// Describes one agent build offered as update (ADR-0023). The exact JSON bytes are signed with the release key
/// (ECDSA P-256 / SHA-256, IEEE P1363); the server only stores and serves manifest, signature and file.
/// </summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("build")] long Build,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size)
{
    public const string ProductName = "MKITS-SupportSuite";

    /// <summary>Upper limit for an update file (the single-file EXE is ~50 MB).</summary>
    public const long MaxSize = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Creates the manifest for a file (used by the release signing step).</summary>
    public static UpdateManifest ForFile(string path, string platform, long build, string? version)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new UpdateManifest(ProductName, platform, build, version, hash, new FileInfo(path).Length);
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Signs the manifest bytes with the release key (P1363, 64 bytes).</summary>
    public static byte[] Sign(byte[] manifestBytes, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Returns the manifest if <paramref name="signature"/> is valid for the exact <paramref name="manifestBytes"/> under the
    /// trusted key and all fields are plausible; otherwise <c>null</c>.
    /// </summary>
    public static UpdateManifest? Verify(byte[] manifestBytes, byte[] signature, byte[]? trustedKey)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(signature);
        if (trustedKey is null || signature.Length != 64 || manifestBytes.Length is 0 or > 4096)
        {
            return null;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(trustedKey, out _);
            if (key.KeySize != 256
                || !key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json);
            return manifest is not null && manifest.IsPlausible() ? manifest : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Platform} build {Build} ({Version}, {Size} bytes)");

    private bool IsPlausible() =>
        Product == ProductName
        && !string.IsNullOrEmpty(Platform)
        && Build > 0
        && Size is > 0 and <= MaxSize
        && Sha256 is { Length: 64 } && Sha256.All(char.IsAsciiHexDigitLower);
}
