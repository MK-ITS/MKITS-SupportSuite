// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MKITS.SupportSuite.Common.Crypto;

/// <summary>Which end of the session plane this process is (decides send and receive keys).</summary>
public enum E2ERole
{
    Agent,
    Viewer,
}

/// <summary>
/// An ephemeral ECDH P-256 key pair for one key exchange (docs/protocol.md §6). Dispose after
/// <see cref="SessionCrypto.Establish"/>; the private key never leaves this process.
/// </summary>
public sealed class E2EKeyPair : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>SubjectPublicKeyInfo (DER) – the value sent in <c>KeyExchange.ephemeral_public_key</c>.</summary>
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    internal byte[] DeriveSharedSecret(byte[] peerPublicKey)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out var read);
        if (read != peerPublicKey.Length || peer.KeySize != 256)
        {
            throw new CryptographicException("Peer key is not a P-256 SubjectPublicKeyInfo.");
        }

        // Raw x-coordinate, identical to WebCrypto deriveBits(ECDH) – required for browser interoperability.
        return _key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Key agreement, payload encryption and the security code (SAS) of the session plane (docs/protocol.md §6).
/// The same construction is implemented in the browser (WebCrypto) and on Android.
/// </summary>
public static class SessionCrypto
{
    internal const int KeySize = 32;
    internal const int TagSize = 16;
    internal const int NonceSize = 12;

    private static readonly byte[] KdfLabel = Encoding.ASCII.GetBytes("mkits/v0/e2e");
    private static readonly byte[] SasLabel = Encoding.ASCII.GetBytes("mkits/v0/sas");

    /// <summary>
    /// Derives the two direction keys and returns a ready <see cref="SessionCipher"/>.
    /// <paramref name="agentPublicKey"/> and <paramref name="viewerPublicKey"/> are the SPKI values as exchanged.
    /// </summary>
    public static SessionCipher Establish(E2ERole role, E2EKeyPair own, string sessionId, byte[] agentPublicKey, byte[] viewerPublicKey)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(agentPublicKey);
        ArgumentNullException.ThrowIfNull(viewerPublicKey);

        var peerPublicKey = role == E2ERole.Agent ? viewerPublicKey : agentPublicKey;
        var shared = own.DeriveSharedSecret(peerPublicKey);
        try
        {
            var keys = HKDF.DeriveKey(
                HashAlgorithmName.SHA256, shared, 2 * KeySize,
                salt: Encoding.UTF8.GetBytes(sessionId),
                info: Concat(KdfLabel, agentPublicKey, viewerPublicKey));

            var agentToViewer = keys.AsSpan(0, KeySize).ToArray();
            var viewerToAgent = keys.AsSpan(KeySize, KeySize).ToArray();
            CryptographicOperations.ZeroMemory(keys);

            return role == E2ERole.Agent
                ? new SessionCipher(sessionId, sendKey: agentToViewer, receiveKey: viewerToAgent)
                : new SessionCipher(sessionId, sendKey: viewerToAgent, receiveKey: agentToViewer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>
    /// The 6-digit security code both sides display, e.g. <c>"042 917"</c>. Equal codes prove that both sides
    /// see the same public keys, i.e. there is no man in the middle (not even a compromised server).
    /// </summary>
    public static string ComputeSecurityCode(string sessionId, byte[] agentPublicKey, byte[] viewerPublicKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(agentPublicKey);
        ArgumentNullException.ThrowIfNull(viewerPublicKey);

        var digest = SHA256.HashData(Concat(SasLabel, Encoding.UTF8.GetBytes(sessionId), agentPublicKey, viewerPublicKey));
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000;
        var digits = value.ToString("D6", CultureInfo.InvariantCulture);
        return $"{digits[..3]} {digits[3..]}";
    }

    /// <summary>Length-prefixed concatenation (4-byte big-endian length before each part) – unambiguous.</summary>
    internal static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => 4 + p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset), part.Length);
            part.CopyTo(result, offset + 4);
            offset += 4 + part.Length;
        }

        return result;
    }
}

/// <summary>
/// Encrypts outgoing and decrypts incoming <c>SessionData</c> payloads with AES-256-GCM.
/// Nonce = channel (32 bit) ‖ sequence (64 bit); associated data = session id ‖ channel ‖ sequence.
/// Sequence numbers are per direction and channel and must strictly increase (replay protection).
/// Not thread-safe per direction: use one sender and one receiver loop.
/// </summary>
public sealed class SessionCipher : IDisposable
{
    private readonly byte[] _sessionId;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly Dictionary<uint, ulong> _lastSent = [];
    private readonly Dictionary<uint, ulong> _lastReceived = [];

    internal SessionCipher(string sessionId, byte[] sendKey, byte[] receiveKey)
    {
        _sessionId = Encoding.UTF8.GetBytes(sessionId);
        _send = new AesGcm(sendKey, SessionCrypto.TagSize);
        _receive = new AesGcm(receiveKey, SessionCrypto.TagSize);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> for <paramref name="channel"/> and returns (sequence, ciphertext ‖ tag).</summary>
    public (ulong Sequence, byte[] Ciphertext) Encrypt(uint channel, ReadOnlySpan<byte> plaintext)
    {
        _lastSent.TryGetValue(channel, out var last);
        if (last == ulong.MaxValue)
        {
            throw new CryptographicException("Sequence space exhausted; a new key exchange is required.");
        }

        var sequence = last + 1;
        _lastSent[channel] = sequence;

        var output = new byte[plaintext.Length + SessionCrypto.TagSize];
        Span<byte> nonce = stackalloc byte[SessionCrypto.NonceSize];
        WriteNonce(nonce, channel, sequence);
        _send.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), AssociatedData(channel, sequence));
        return (sequence, output);
    }

    /// <summary>
    /// Decrypts a payload. Returns <c>null</c> if authentication fails or the sequence number is not greater than
    /// the last accepted one on this channel (replayed, reordered or forged). Never throws for hostile input.
    /// </summary>
    public byte[]? Decrypt(uint channel, ulong sequence, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < SessionCrypto.TagSize || sequence == 0)
        {
            return null;
        }

        if (_lastReceived.TryGetValue(channel, out var last) && sequence <= last)
        {
            return null;
        }

        var length = ciphertext.Length - SessionCrypto.TagSize;
        var plaintext = new byte[length];
        Span<byte> nonce = stackalloc byte[SessionCrypto.NonceSize];
        WriteNonce(nonce, channel, sequence);
        try
        {
            _receive.Decrypt(nonce, ciphertext[..length], ciphertext[length..], plaintext, AssociatedData(channel, sequence));
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        _lastReceived[channel] = sequence;
        return plaintext;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }

    private static void WriteNonce(Span<byte> nonce, uint channel, ulong sequence)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce, channel);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], sequence);
    }

    private byte[] AssociatedData(uint channel, ulong sequence)
    {
        var data = new byte[_sessionId.Length + 12];
        _sessionId.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(_sessionId.Length), channel);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(_sessionId.Length + 4), sequence);
        return data;
    }
}
