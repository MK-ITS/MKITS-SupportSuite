// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Security.Cryptography;
using MKITS.SupportSuite.Agent.Core.Updates;

// Usage: UpdateSigner <file> <platform> <build> <version> <private-key.pem>
if (args.Length != 5 || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var build) || build <= 0)
{
    Console.Error.WriteLine("Usage: UpdateSigner <file> <platform> <build> <version> <private-key.pem>");
    return 2;
}

var (file, platform, version, keyFile) = (args[0], args[1], args[3], args[4]);
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(keyFile));
if (key.KeySize != 256)
{
    Console.Error.WriteLine("The release key must be ECDSA P-256.");
    return 3;
}

var manifest = UpdateManifest.ForFile(file, platform, build, version);
var bytes = manifest.ToBytes();
var signature = UpdateManifest.Sign(bytes, key);

// Self-check with the public half, exactly as the agent verifies.
if (UpdateManifest.Verify(bytes, signature, key.ExportSubjectPublicKeyInfo()) is null)
{
    Console.Error.WriteLine("Verification of the new signature failed.");
    return 4;
}

File.WriteAllBytes(file + ".manifest.json", bytes);
File.WriteAllText(file + ".manifest.sig", Convert.ToBase64String(signature));
Console.WriteLine($"Signed {manifest}; public key: {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
return 0;
