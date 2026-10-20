// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Common.Tests.Protocol;

public sealed class PayloadChannelsTests
{
    [Fact]
    public void For_MapsEveryPayloadType()
    {
        foreach (var bodyCase in Enum.GetValues<SessionPayload.BodyOneofCase>().Where(c => c != SessionPayload.BodyOneofCase.None))
        {
            var payload = Create(bodyCase);
            Assert.NotEqual(Channel.Unspecified, PayloadChannels.For(payload));
        }
    }

    [Fact]
    public void Matches_RejectsWrongChannel()
    {
        var chat = new SessionPayload { ChatMessage = new ChatMessage { Text = "hi" } };

        Assert.True(PayloadChannels.Matches((uint)Channel.Chat, chat));
        Assert.False(PayloadChannels.Matches((uint)Channel.Input, chat));
        Assert.False(PayloadChannels.Matches(0, new SessionPayload()));
    }

    private static SessionPayload Create(SessionPayload.BodyOneofCase bodyCase)
    {
        // Set the oneof field through reflection descriptors so new payload types are covered automatically.
        var payload = new SessionPayload();
        var field = SessionPayload.Descriptor.FindFieldByNumber((int)bodyCase);
        var message = (Google.Protobuf.IMessage)Activator.CreateInstance(field.MessageType.ClrType)!;
        field.Accessor.SetValue(payload, message);
        return payload;
    }
}
