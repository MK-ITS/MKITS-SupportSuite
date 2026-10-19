// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using MKITS.SupportSuite.Common.Crypto;

namespace MKITS.SupportSuite.Common.Tests.Crypto;

public sealed class SessionCryptoTests : IDisposable
{
    private const string SessionId = "0192f0c4-1c2d-7a3b-8000-000000000001";

    private readonly E2EKeyPair _agentKey = new();
    private readonly E2EKeyPair _viewerKey = new();
    private readonly SessionCipher _agent;
    private readonly SessionCipher _viewer;

    public SessionCryptoTests()
    {
        _agent = SessionCrypto.Establish(E2ERole.Agent, _agentKey, SessionId, _agentKey.PublicKey, _viewerKey.PublicKey);
        _viewer = SessionCrypto.Establish(E2ERole.Viewer, _viewerKey, SessionId, _agentKey.PublicKey, _viewerKey.PublicKey);
    }

    public void Dispose()
    {
        _agent.Dispose();
        _viewer.Dispose();
        _agentKey.Dispose();
        _viewerKey.Dispose();
    }

    [Fact]
    public void Encrypt_Decrypt_BothDirections()
    {
        var (s1, c1) = _agent.Encrypt(1, "frame"u8);
        var (s2, c2) = _viewer.Encrypt(2, "click"u8);

        Assert.Equal("frame"u8.ToArray(), _viewer.Decrypt(1, s1, c1));
        Assert.Equal("click"u8.ToArray(), _agent.Decrypt(2, s2, c2));
    }

    [Fact]
    public void Directions_UseDifferentKeys()
    {
        var (sequence, ciphertext) = _agent.Encrypt(6, "hi"u8);

        // The agent cannot decrypt its own message: the receive key is the other direction's key.
        Assert.Null(_agent.Decrypt(6, sequence, ciphertext));
    }

    [Fact]
    public void Sequence_StartsAtOne_AndIncreasesPerChannel()
    {
        Assert.Equal(1UL, _agent.Encrypt(1, [1]).Sequence);
        Assert.Equal(2UL, _agent.Encrypt(1, [1]).Sequence);
        Assert.Equal(1UL, _agent.Encrypt(2, [1]).Sequence);
    }

    [Fact]
    public void Decrypt_Replay_IsRejected()
    {
        var (sequence, ciphertext) = _agent.Encrypt(1, "frame"u8);

        Assert.NotNull(_viewer.Decrypt(1, sequence, ciphertext));
        Assert.Null(_viewer.Decrypt(1, sequence, ciphertext));
    }

    [Fact]
    public void Decrypt_OldSequenceAfterNewer_IsRejected()
    {
        var first = _agent.Encrypt(1, [1]);
        var second = _agent.Encrypt(1, [2]);

        Assert.NotNull(_viewer.Decrypt(1, second.Sequence, second.Ciphertext));
        Assert.Null(_viewer.Decrypt(1, first.Sequence, first.Ciphertext));
    }

    [Fact]
    public void Decrypt_Tampered_IsRejected()
    {
        var (sequence, ciphertext) = _agent.Encrypt(1, "frame"u8);
        ciphertext[0] ^= 0x01;

        Assert.Null(_viewer.Decrypt(1, sequence, ciphertext));
    }

    [Fact]
    public void Decrypt_WrongChannelOrSequence_IsRejected()
    {
        var (sequence, ciphertext) = _agent.Encrypt(1, "frame"u8);

        // Channel and sequence are bound by nonce and associated data – a relay cannot re-label frames.
        Assert.Null(_viewer.Decrypt(2, sequence, ciphertext));
        Assert.Null(_viewer.Decrypt(1, sequence + 1, ciphertext));
    }

    [Fact]
    public void Decrypt_Garbage_ReturnsNull()
    {
        Assert.Null(_viewer.Decrypt(1, 1, []));
        Assert.Null(_viewer.Decrypt(1, 1, new byte[5]));
        Assert.Null(_viewer.Decrypt(1, 0, new byte[32]));
    }

    [Fact]
    public void FailedDecrypt_DoesNotAdvanceReplayWindow()
    {
        var (sequence, ciphertext) = _agent.Encrypt(1, "frame"u8);
        var forged = (byte[])ciphertext.Clone();
        forged[^1] ^= 0x01;

        Assert.Null(_viewer.Decrypt(1, sequence, forged));
        Assert.NotNull(_viewer.Decrypt(1, sequence, ciphertext));
    }

    [Fact]
    public void DifferentSession_CannotDecrypt()
    {
        using var other = SessionCrypto.Establish(E2ERole.Viewer, _viewerKey, "another-session", _agentKey.PublicKey, _viewerKey.PublicKey);
        var (sequence, ciphertext) = _agent.Encrypt(1, "frame"u8);

        Assert.Null(other.Decrypt(1, sequence, ciphertext));
    }

    [Fact]
    public void ManInTheMiddle_IsVisibleInSecurityCode()
    {
        using var mallory = new E2EKeyPair();

        var agentView = SessionCrypto.ComputeSecurityCode(SessionId, _agentKey.PublicKey, mallory.PublicKey);
        var viewerView = SessionCrypto.ComputeSecurityCode(SessionId, mallory.PublicKey, _viewerKey.PublicKey);
        var honest = SessionCrypto.ComputeSecurityCode(SessionId, _agentKey.PublicKey, _viewerKey.PublicKey);

        Assert.Matches("^[0-9]{3} [0-9]{3}$", honest);
        Assert.Equal(honest, SessionCrypto.ComputeSecurityCode(SessionId, _agentKey.PublicKey, _viewerKey.PublicKey));
        Assert.NotEqual(agentView, viewerView);
    }

    [Fact]
    public void Establish_InvalidPeerKey_Throws()
    {
        using var key = new E2EKeyPair();

        Assert.ThrowsAny<CryptographicException>(() => SessionCrypto.Establish(E2ERole.Agent, key, SessionId, key.PublicKey, [1, 2, 3]));
    }

    [Fact]
    public void Establish_OtherCurve_Throws()
    {
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        using var key = new E2EKeyPair();

        Assert.ThrowsAny<CryptographicException>(() =>
            SessionCrypto.Establish(E2ERole.Agent, key, SessionId, key.PublicKey, p384.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void Concat_IsLengthPrefixed_AndUnambiguous()
    {
        Assert.NotEqual(SessionCrypto.Concat([1, 2], [3]), SessionCrypto.Concat([1], [2, 3]));
        Assert.Equal([0, 0, 0, 1, 7], SessionCrypto.Concat([7]));
    }
}
