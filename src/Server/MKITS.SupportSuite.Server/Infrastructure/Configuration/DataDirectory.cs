// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Infrastructure.Configuration;

/// <summary>
/// Resolves and creates the persistent data directory and its well-known subdirectories.
/// Layout is documented in docs/architecture.md (section "Persistent data").
/// </summary>
public sealed class DataDirectory
{
    public DataDirectory(IOptions<MkitsOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        RootPath = Path.GetFullPath(options.Value.DataDirectory, environment.ContentRootPath);
    }

    /// <summary>Absolute path of the data directory.</summary>
    public string RootPath { get; }

    /// <summary>Data Protection keys and server key material.</summary>
    public string KeysPath => Path.Combine(RootPath, "keys");

    /// <summary>Encrypted session recordings.</summary>
    public string RecordingsPath => Path.Combine(RootPath, "recordings");

    /// <summary>Agent packages offered for download and update.</summary>
    public string AgentsPath => Path.Combine(RootPath, "agents");

    /// <summary>SQLite database file.</summary>
    public string DatabasePath => Path.Combine(RootPath, "mkits.db");

    /// <summary>Creates the directory tree if it does not exist yet.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(KeysPath);
        Directory.CreateDirectory(RecordingsPath);
        Directory.CreateDirectory(AgentsPath);
    }
}
