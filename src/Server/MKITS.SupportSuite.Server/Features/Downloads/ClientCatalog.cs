// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Infrastructure.Configuration;

namespace MKITS.SupportSuite.Server.Features.Downloads;

/// <summary>Platforms an ad-hoc client can be downloaded for.</summary>
public enum ClientPlatform
{
    WindowsX64,
    LinuxX64,
    LinuxArm64,
    Android,
}

/// <summary>Where a client package lives and how it is named on download.</summary>
public sealed record ClientPackage(ClientPlatform Platform, string RelativePath, string Extension, bool CodeInFileName);

/// <summary>A package that is actually present on this server.</summary>
public sealed record AvailableClient(ClientPackage Package, string FullPath, long SizeBytes);

/// <summary>A signed agent update next to a package: <c>&lt;file&gt;.manifest.json</c> and <c>&lt;file&gt;.manifest.sig</c> (ADR-0023).</summary>
public sealed record AgentUpdate(AvailableClient Client, byte[] Manifest, string Signature);

/// <summary>
/// The ad-hoc client packages offered on the portal. Packages are looked up in <c>/data/agents</c> first
/// (operator-provided, e.g. a newer or self-built version) and then in the <c>agents</c> folder of the
/// application (bundled with the image once releases exist). Layout:
/// <c>windows-x64/MKITS-Support.exe</c>, <c>linux-x64/MKITS-Support.AppImage</c>,
/// <c>linux-arm64/MKITS-Support.AppImage</c>, <c>android/MKITS-Support.apk</c>.
/// </summary>
public sealed class ClientCatalog(DataDirectory dataDirectory, IHostEnvironment environment)
{
    public static readonly IReadOnlyList<ClientPackage> Packages =
    [
        new(ClientPlatform.WindowsX64, Path.Combine("windows-x64", "MKITS-Support.exe"), ".exe", CodeInFileName: true),
        new(ClientPlatform.LinuxX64, Path.Combine("linux-x64", "MKITS-Support.AppImage"), ".AppImage", CodeInFileName: true),
        new(ClientPlatform.LinuxArm64, Path.Combine("linux-arm64", "MKITS-Support.AppImage"), ".AppImage", CodeInFileName: true),

        // The Android app asks for the code (an APK's name is not visible to the app after installation).
        new(ClientPlatform.Android, Path.Combine("android", "MKITS-Support.apk"), ".apk", CodeInFileName: false),
    ];

    /// <summary>Directories searched in order.</summary>
    public IReadOnlyList<string> SearchPaths => [dataDirectory.AgentsPath, Path.Combine(environment.ContentRootPath, "agents")];

    /// <summary>Packages present on disk (checked on every call – files may be replaced while running).</summary>
    public IReadOnlyList<AvailableClient> GetAvailable() =>
        Packages.Select(Find).Where(c => c is not null).Select(c => c!).ToList();

    public AvailableClient? Get(ClientPlatform platform) =>
        Packages.Where(p => p.Platform == platform).Select(Find).FirstOrDefault();

    /// <summary>Platform for a folder name in the agents directory, e.g. <c>windows-x64</c> (also the update API name).</summary>
    public static ClientPlatform? FromFolder(string? folder) =>
        Packages.Where(p => Path.GetDirectoryName(p.RelativePath) == folder).Select(p => (ClientPlatform?)p.Platform).FirstOrDefault();

    /// <summary>
    /// The signed update for a platform: only if manifest and signature sit next to the package that is actually served
    /// (an operator-provided file without them disables updates instead of mixing files of different builds).
    /// The server never checks the signature itself – agents do, with the release key compiled into them.
    /// </summary>
    public AgentUpdate? GetUpdate(ClientPlatform platform)
    {
        if (Get(platform) is not { } client)
        {
            return null;
        }

        var manifest = client.FullPath + ".manifest.json";
        var signature = client.FullPath + ".manifest.sig";
        if (!File.Exists(manifest) || !File.Exists(signature) || new FileInfo(manifest).Length > 4096 || new FileInfo(signature).Length > 1024)
        {
            return null;
        }

        return new AgentUpdate(client, File.ReadAllBytes(manifest), File.ReadAllText(signature).Trim());
    }

    private AvailableClient? Find(ClientPackage package)
    {
        foreach (var root in SearchPaths)
        {
            var file = new FileInfo(Path.Combine(root, package.RelativePath));
            if (file.Exists && file.Length > 0)
            {
                return new AvailableClient(package, file.FullName, file.Length);
            }
        }

        return null;
    }
}

/// <summary>Guesses the visitor's platform from the User-Agent (only used to highlight the right download).</summary>
public static class PlatformDetector
{
    public static ClientPlatform? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return null;
        }

        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return ClientPlatform.Android;
        }

        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return ClientPlatform.WindowsX64;
        }

        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("X11", StringComparison.OrdinalIgnoreCase))
        {
            return userAgent.Contains("aarch64", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("arm64", StringComparison.OrdinalIgnoreCase)
                ? ClientPlatform.LinuxArm64
                : ClientPlatform.LinuxX64;
        }

        return null; // macOS, iOS, … are not supported
    }
}
