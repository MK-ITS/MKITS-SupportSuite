// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;

namespace MKITS.SupportSuite.Agent.Core.Devices;

/// <summary>
/// Long-term identity of a permanent device: the id the server assigned at enrollment and the ECDSA P-256 key
/// pair whose public half the server stored. The private key never leaves the device.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(Guid deviceId, ECDsa key)
    {
        DeviceId = deviceId;
        _key = key;
    }

    public Guid DeviceId { get; }

    /// <summary>SubjectPublicKeyInfo (DER), as sent in <c>Enrollment.device_public_key</c>.</summary>
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    /// <summary>A fresh key pair for enrolling; the id is known only after the server accepted it.</summary>
    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static DeviceIdentity FromKey(Guid deviceId, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new DeviceIdentity(deviceId, key);
    }

    /// <summary>Restores an identity from <see cref="ExportPrivateKey"/>. Throws <see cref="CryptographicException"/> for invalid data.</summary>
    public static DeviceIdentity Import(Guid deviceId, ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
            if (key.KeySize != 256)
            {
                throw new CryptographicException("The device key must be ECDSA P-256.");
            }

            return new DeviceIdentity(deviceId, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>PKCS#8 private key – only for the platform's protected store.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    /// <summary>ECDSA P-256 / SHA-256 in IEEE P1363 format (r‖s, 64 bytes) – the format the server and WebCrypto verify.</summary>
    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
