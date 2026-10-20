// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Common.Protocol;

/// <summary>
/// Version of the wire protocol spoken between server, agents and viewers.
/// </summary>
/// <remarks>
/// Rules (docs/protocol.md §8):
/// <list type="bullet">
///   <item><description>Increase <see cref="Major"/> on breaking changes; peers with different major versions must refuse to connect.</description></item>
///   <item><description>Increase <see cref="Minor"/> on backwards-compatible additions (new optional messages or fields).</description></item>
/// </list>
/// </remarks>
public static class ProtocolVersion
{
    public const int Major = 0;

    public const int Minor = 2;

    /// <summary>Returns <c>true</c> if a peer speaking the given major version can talk to this build.</summary>
    public static bool IsCompatible(int peerMajor) => peerMajor == Major;

    /// <summary>Version string in the form <c>major.minor</c>.</summary>
    public static string AsString => $"{Major}.{Minor}";
}
