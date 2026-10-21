// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Features.Devices;

namespace MKITS.SupportSuite.Server.Components.Admin;

/// <summary>Form model of a device group.</summary>
public sealed class GroupInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public DeviceConsentPolicy ConsentPolicy { get; set; } = DeviceConsentPolicy.Ask;

    public bool RequireApproval { get; set; }
}
