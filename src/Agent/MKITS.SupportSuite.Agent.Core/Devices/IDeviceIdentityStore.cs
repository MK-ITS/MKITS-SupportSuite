// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Core.Devices;

/// <summary>
/// Persists the device identity. Platforms protect the private key (Windows: DPAPI machine scope in a folder only
/// SYSTEM and Administrators can read; Linux: root-only file).
/// </summary>
public interface IDeviceIdentityStore
{
    /// <summary>The stored identity, or <c>null</c> if the device is not enrolled (or the stored data is unusable).</summary>
    DeviceIdentity? Load();

    void Save(DeviceIdentity identity);

    /// <summary>Forgets the identity (e.g. before enrolling again).</summary>
    void Delete();
}
