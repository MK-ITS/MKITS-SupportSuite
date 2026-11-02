// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Linux.Wayland;

/// <summary>
/// The portal's restore token for the installed agent (in the user's home: <c>~/.local/state/mkits-agent</c>, mode
/// 0600). With it the desktop can start the next remote desktop session without asking again – as far as the desktop
/// supports and allows that. The portal hands out a new token every time; the old one becomes invalid.
/// </summary>
internal sealed class PortalTokenStore(string path)
{
    private const int MaxLength = 1024;

    /// <summary><c>$XDG_STATE_HOME/mkits-agent/portal-restore-token</c> (default <c>~/.local/state/…</c>).</summary>
    public static PortalTokenStore ForCurrentUser()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathFullyQualified(state))
        {
            state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        }

        return new PortalTokenStore(Path.Combine(state, "mkits-agent", "portal-restore-token"));
    }

    public string? Load()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxLength)
            {
                return null;
            }

            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 && token.All(c => c is > ' ' and < (char)0x7F) ? token : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string? token)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                File.Delete(path);
                return;
            }

            var folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temp = path + ".tmp";
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var writer = new StreamWriter(temp, System.Text.Encoding.ASCII, options))
            {
                writer.Write(token);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the dialog appears again next time
        }
    }
}
