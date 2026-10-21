// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using System.Text.Json;

namespace MKITS.SupportSuite.Agent.Core.Devices;

/// <summary>
/// Stores the identity as a small JSON file. The private key passes through <c>protect</c>/<c>unprotect</c>
/// (e.g. DPAPI on Windows); on Unix the file is created with mode 0600. Writes are atomic (temp file + rename).
/// </summary>
public sealed class FileDeviceIdentityStore(string path, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    : IDeviceIdentityStore
{
    private const int FormatVersion = 1;

    public DeviceIdentity? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllBytes(path));
            if (stored is not { Version: FormatVersion } || !Guid.TryParse(stored.DeviceId, out var deviceId) || stored.Key is null)
            {
                return null;
            }

            var key = Convert.FromBase64String(stored.Key);
            var plain = unprotect is null ? key : unprotect(key);
            try
            {
                return DeviceIdentity.Import(deviceId, plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // unusable – the device has to be enrolled again
        }
    }

    public void Save(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var plain = identity.ExportPrivateKey();
        byte[] key;
        try
        {
            key = protect is null ? (byte[])plain.Clone() : protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(FormatVersion, identity.DeviceId.ToString(), Convert.ToBase64String(key)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(json);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record StoredIdentity(int Version, string? DeviceId, string? Key);
}
