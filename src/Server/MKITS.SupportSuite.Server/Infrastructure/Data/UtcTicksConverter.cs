// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MKITS.SupportSuite.Server.Infrastructure.Data;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as UTC ticks (SQLite INTEGER): lossless, sortable and comparable in SQL.
/// Values are read back with offset zero – all timestamps in this application are UTC.
/// </summary>
/// <remarks>
/// EF Core's built-in DateTimeOffsetToBinaryConverter reduces precision, which would break
/// anything that hashes timestamps (audit log chain).
/// </remarks>
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
