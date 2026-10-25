// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text.Json;
using MKITS.SupportSuite.Agent.Core.Devices;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// Configuration of the installed agent (<c>/etc/mkits-agent/agent.json</c>, mode 0600 in a 0700 folder: root only).
/// There is no DPAPI on Linux: the enrollment token is protected by file permissions and removed once the device is
/// enrolled (ADR-0025).
/// </summary>
internal sealed record AgentConfig(string ServerUri, string? Token)
{
    public static AgentConfig? Load(string path)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllBytes(path));
            return config is not null && ServerAddress.Parse(config.ServerUri) is not null ? config : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Atomic write; folder 0700 and file 0600 (created like that, never readable by others in between).</summary>
    public void Save(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var temp = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(this));
        }

        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); // an old temp file may have had other bits
        File.Move(temp, path, overwrite: true);
    }
}
