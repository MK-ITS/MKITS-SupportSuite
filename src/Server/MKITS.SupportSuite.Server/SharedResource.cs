// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server;

/// <summary>
/// Marker type for the server-wide UI strings in <c>Resources/SharedResource.resx</c> (English, default)
/// and <c>Resources/SharedResource.de.resx</c> (German). Use via <c>IStringLocalizer&lt;SharedResource&gt;</c>.
/// </summary>
/// <remarks>Must stay in the root namespace so the resource lookup path matches.</remarks>
#pragma warning disable CA1052 // Marker type for localization – must be a non-static class to be usable as a generic argument.
public sealed class SharedResource
#pragma warning restore CA1052
{
    private SharedResource()
    {
    }
}
