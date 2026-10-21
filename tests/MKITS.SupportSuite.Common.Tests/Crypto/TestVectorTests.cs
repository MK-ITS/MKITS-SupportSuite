// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text.Json;
using MKITS.SupportSuite.Common.Crypto;

namespace MKITS.SupportSuite.Common.Tests.Crypto;

/// <summary>
/// The shared test vector proto/testvectors/e2e-v0.json (generated with WebCrypto) proves that the C# and the
/// browser implementation are byte-compatible. The browser side checks the same file (src/Viewer/web/test).
/// </summary>
public sealed class TestVectorTests
{
    private static readonly JsonElement Vector = Load();

    [Fact]
    public void SecurityCode_MatchesVector()
    {
        Assert.Equal(
            Text("securityCode"),
            SessionCrypto.ComputeSecurityCode(Text("sessionId"), Bytes("agentPublicKeySpki"), Bytes("viewerPublicKeySpki")));
    }

    [Fact]
    public void AgentSide_DecryptsViewerCiphertext_AndProducesSameAgentCiphertext()
    {
        using var agent = E2EKeyPair.FromPkcs8(Bytes("agentPrivateKeyPkcs8"));
        Assert.Equal(Bytes("agentPublicKeySpki"), agent.PublicKey);

        using var cipher = SessionCrypto.Establish(E2ERole.Agent, agent, Text("sessionId"), Bytes("agentPublicKeySpki"), Bytes("viewerPublicKeySpki"));
        var channel = (uint)Vector.GetProperty("channel").GetInt32();

        Assert.Equal(Bytes("plaintext"), cipher.Decrypt(channel, 1, Bytes("ciphertextViewerToAgent")));

        var (sequence, ciphertext) = cipher.Encrypt(channel, Bytes("plaintext"));
        Assert.Equal(1UL, sequence);
        Assert.Equal(Bytes("ciphertextAgentToViewer"), ciphertext);
    }

    private static string Text(string name) => Vector.GetProperty(name).GetString()!;

    private static byte[] Bytes(string name) => Convert.FromHexString(Text(name));

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "proto", "testvectors", "e2e-v0.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "proto", "testvectors", "e2e-v0.json")));
        return document.RootElement.Clone();
    }
}
