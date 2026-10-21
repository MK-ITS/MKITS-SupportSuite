// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Common.Tests.Protocol;

public sealed class ChannelPermissionsTests
{
    [Theory]
    [InlineData(Channel.Video, ViewerPermissions.View)]
    [InlineData(Channel.Display, ViewerPermissions.View)]
    [InlineData(Channel.Chat, ViewerPermissions.View)]
    [InlineData(Channel.Input, ViewerPermissions.Control)]
    [InlineData(Channel.Clipboard, ViewerPermissions.Clipboard)]
    [InlineData(Channel.File, ViewerPermissions.Files)]
    [InlineData(Channel.Shell, ViewerPermissions.Shell)]
    [InlineData(Channel.Unspecified, ViewerPermissions.All)]
    [InlineData((Channel)99, ViewerPermissions.All)]
    public void Required_MapsChannels(Channel channel, ViewerPermissions expected) =>
        Assert.Equal(expected, ChannelPermissions.Required((uint)channel));

    [Fact]
    public void Names_RoundTrip_AndUnknownNamesAreIgnored()
    {
        var names = ChannelPermissions.ToNames(ViewerPermissions.View | ViewerPermissions.Shell);

        Assert.Equal(new[] { "view", "shell" }, names);
        Assert.Equal(ViewerPermissions.View | ViewerPermissions.Shell, ChannelPermissions.Parse(names));
        Assert.Equal(ViewerPermissions.Control, ChannelPermissions.Parse(["control", "root", "VIEW"]));
        Assert.Equal(ViewerPermissions.All, ChannelPermissions.Parse(ChannelPermissions.ToNames(ViewerPermissions.All)));
    }
}
