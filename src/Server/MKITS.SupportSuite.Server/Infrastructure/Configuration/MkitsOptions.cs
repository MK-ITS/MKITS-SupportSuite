// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Infrastructure.Configuration;

/// <summary>
/// General server settings, bound from the configuration section <c>Mkits</c>.
/// Environment variables use double underscores, e.g. <c>Mkits__PublicBaseUrl</c>.
/// </summary>
public sealed class MkitsOptions
{
    public const string SectionName = "Mkits";

    /// <summary>
    /// Public URL under which users and agents reach the server, e.g. <c>https://support.example.com</c>.
    /// Used for links, agent download file names and the WebSocket address handed to clients.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// Directory for persistent data: database, keys, recordings, agent packages.
    /// Relative paths are resolved against the application's content root. Docker: <c>/data</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "data";
}
