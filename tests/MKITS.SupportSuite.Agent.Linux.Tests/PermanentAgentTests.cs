// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics;
using System.Text;
using MKITS.SupportSuite.Agent.Linux.Native;
using MKITS.SupportSuite.Agent.Linux.Permanent;

namespace MKITS.SupportSuite.Agent.Linux.Tests;

/// <summary>Building blocks of the installed agent that need no root and no logind (ADR-0025).</summary>
public sealed class PermanentAgentTests : IDisposable
{
    private static readonly byte[] Cookie = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly byte[] OtherCookie = [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9];
    private static readonly string[] Server = ["--install", "--server", "https://support.example.com/path", "--token", " abc "];
    private static readonly string[] NoToken = ["--install", "--server", "https://support.example.com"];
    private static readonly string[] Reenroll = ["--install", "--server", "https://support.example.com", "--reenroll"];
    private static readonly string[] Ftp = ["--install", "--server", "ftp://x", "--token", "t"];
    private static readonly string[] SessionIds = ["2", "c3"];
    private static readonly string[] RunUserArguments = ["-u", "alice", "--", "/opt/mkits-agent/mkits-agent", "--agent-user"];
    private static readonly string[] SplitArguments = ["a", "b c"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mkits-linux-" + Guid.NewGuid().ToString("N"));

    public PermanentAgentTests() => Directory.CreateDirectory(_directory);

    private static uint MyUid => LibcNative.GetEffectiveUserId();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // ------------------------------------------------------------------ Xauthority

    [Fact]
    public void XAuthority_WriteAndParse_RoundTrip_StopsAtTruncatedEntry()
    {
        var entry = new XAuthorityEntry(XAuthority.FamilyLocal, Encoding.ASCII.GetBytes("host"), "0", XAuthority.MitMagicCookie, Cookie);
        var bytes = XAuthority.Write([entry, entry with { Number = "1" }]);

        var parsed = XAuthority.Parse(bytes);
        var truncated = XAuthority.Parse(bytes.AsSpan(0, bytes.Length - 3));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("1", parsed[1].Number);
        Assert.Equal(Cookie, parsed[0].Data);
        Assert.Single(truncated);
    }

    [Fact]
    public void XAuthority_FindCookie_PrefersTheDisplayNumber_AndIgnoresOtherHosts()
    {
        var entries = new[]
        {
            new XAuthorityEntry(XAuthority.FamilyLocal, Encoding.ASCII.GetBytes("other-host"), "0", XAuthority.MitMagicCookie, OtherCookie),
            new XAuthorityEntry(XAuthority.FamilyWild, [], string.Empty, XAuthority.MitMagicCookie, OtherCookie),
            new XAuthorityEntry(XAuthority.FamilyLocal, Encoding.ASCII.GetBytes("myhost"), "0", "XDM-AUTHORIZATION-1", OtherCookie),
            new XAuthorityEntry(XAuthority.FamilyLocal, Encoding.ASCII.GetBytes("MYHOST"), "0", XAuthority.MitMagicCookie, Cookie),
        };

        Assert.Equal(Cookie, XAuthority.FindCookie(entries, "0", "myhost"));
        Assert.Equal(OtherCookie, XAuthority.FindCookie(entries, "5", "myhost")); // entry without number as fallback
        Assert.Null(XAuthority.FindCookie(entries[..1], "0", "myhost"));
    }

    [Theory]
    [InlineData(":0", "0")]
    [InlineData(":1.0", "1")]
    [InlineData("unix:12", "12")]
    [InlineData("remote:0", null)]
    [InlineData("localhost:0", null)]
    [InlineData(":", null)]
    [InlineData(":abc", null)]
    public void XAuthority_DisplayNumber(string display, string? expected) => Assert.Equal(expected, XAuthority.DisplayNumber(display));

    // ------------------------------------------------------------------ logind

    [Fact]
    public void LoginSessions_ParseListAndDetails()
    {
        const string List = "     2 1000 alice seat0 tty2\n c3 120 gdm seat0 tty1\n$(evil) 0 root\n";
        const string Details = "Id=2\nUser=1000\nName=alice\nType=x11\nDisplay=:0\nActive=yes\nRemote=no\nClass=user\nState=active\n";

        var ids = LoginSessions.ParseSessionIds(List).ToList();
        var session = LoginSessions.ParseSession(Details);

        Assert.Equal(SessionIds, ids);
        Assert.Equal(new LoginSession("2", 1000, "alice", "x11", ":0", true, false, "user"), session);
        Assert.Null(LoginSessions.ParseSession("Id=5\nUser=nobody\n"));
    }

    [Fact]
    public void LoginSessions_SelectGraphical_TakesTheActiveLocalUserSession_PreferringX11()
    {
        var greeter = new LoginSession("c1", 120, "gdm", "x11", ":1024", true, false, "greeter");
        var ssh = new LoginSession("4", 1000, "alice", "tty", string.Empty, true, true, "user");
        var inactive = new LoginSession("3", 1001, "bob", "x11", ":1", false, false, "user");
        var wayland = new LoginSession("5", 1002, "carol", "wayland", string.Empty, true, false, "user");
        var x11 = new LoginSession("2", 1000, "alice", "x11", ":0", true, false, "user");

        Assert.Equal(x11, LoginSessions.SelectGraphical([greeter, ssh, inactive, wayland, x11]));
        Assert.Equal(wayland, LoginSessions.SelectGraphical([greeter, wayland]));
        Assert.Null(LoginSessions.SelectGraphical([greeter, ssh, inactive]));
    }

    // ------------------------------------------------------------------ desktop lookup

    [Theory]
    [InlineData(":0", true)]
    [InlineData(":10.0", true)]
    [InlineData("localhost:10.0", false)]
    [InlineData("evil.example.com:0", false)]
    [InlineData(":0; rm -rf /", false)]
    [InlineData(null, false)]
    public void DesktopLocator_OnlyLocalDisplays(string? display, bool expected) => Assert.Equal(expected, DesktopLocator.IsLocalDisplay(display));

    [Fact]
    public void DesktopLocator_XServerAuthFile_FromXorgCommandLines()
    {
        string[] lightDm = ["/usr/lib/xorg/Xorg", ":0", "-seat", "seat0", "-auth", "/var/run/lightdm/root/:0", "-nolisten", "tcp"];
        string[] gdm = ["/usr/lib/xorg/Xorg", "vt2", "-displayfd", "3", "-auth", "/run/user/1000/gdm/Xauthority", "-nolisten", "tcp"];
        string[] otherDisplay = ["X", ":1", "-auth", "/tmp/a"];
        string[] notX = ["/usr/bin/evil", ":0", "-auth", "/etc/shadow"];

        Assert.Equal("/var/run/lightdm/root/:0", DesktopLocator.XServerAuthFile(lightDm, ":0"));
        Assert.Equal("/run/user/1000/gdm/Xauthority", DesktopLocator.XServerAuthFile(gdm, ":0"));
        Assert.Null(DesktopLocator.XServerAuthFile(otherDisplay, ":0"));
        Assert.Null(DesktopLocator.XServerAuthFile(notX, ":0"));
    }

    [Fact]
    public void DesktopLocator_SessionEnvironment_CopiesOnlyWhitelistedVariables()
    {
        var environment = new Dictionary<string, string>
        {
            ["DISPLAY"] = ":5",
            ["XAUTHORITY"] = "/home/alice/.Xauthority",
            ["LANG"] = "de_DE.UTF-8",
            ["LD_PRELOAD"] = "/tmp/evil.so",
            ["PATH"] = "/tmp/evil",
        };

        var variables = DesktopLocator.SessionEnvironment(environment, ":0", 1000);

        Assert.Equal(":0", variables["DISPLAY"]); // the display logind names wins
        Assert.Equal("de_DE.UTF-8", variables["LANG"]);
        Assert.Equal("/run/user/1000", variables["XDG_RUNTIME_DIR"]);
        Assert.False(variables.ContainsKey("LD_PRELOAD"));
        Assert.False(variables.ContainsKey("PATH"));
    }

    [Fact]
    public void UserSessionConnector_StartsTheWindowAsTheUser_WithAMinimalEnvironment()
    {
        var session = new LoginSession("2", 1000, "alice", "x11", ":0", true, false, "user");
        var desktop = new UserDesktop(session, new UserAccount(1000, 1000, "alice", "/home/alice"), ":0", Cookie,
            new Dictionary<string, string> { ["DISPLAY"] = ":0", ["LANG"] = "de_DE.UTF-8" });

        var start = UserSessionConnector.CreateStartInfo(desktop, "/opt/mkits-agent/mkits-agent");

        Assert.Equal("/usr/sbin/runuser", start.FileName);
        Assert.Equal(RunUserArguments, start.ArgumentList);
        Assert.Equal(":0", start.Environment["DISPLAY"]);
        Assert.Equal("/home/alice", start.Environment["HOME"]);
        Assert.Equal("alice", start.Environment["USER"]);
        Assert.False(start.Environment.ContainsKey("INVOCATION_ID")); // nothing of the service's own environment
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
    }

    // ------------------------------------------------------------------ files and processes

    [Fact]
    public void HostSystem_ReadOwnedFile_ChecksTheOwnerOfTheOpenedFile()
    {
        var file = Path.Combine(_directory, "auth");
        File.WriteAllBytes(file, Cookie);
        var link = Path.Combine(_directory, "link");
        File.CreateSymbolicLink(link, file);

        Assert.Equal(Cookie, HostSystem.ReadOwnedFile(file, [MyUid], 1024));
        Assert.Equal(Cookie, HostSystem.ReadOwnedFile(link, [MyUid], 1024)); // the target's owner counts
        Assert.Null(HostSystem.ReadOwnedFile(file, [MyUid + 1], 1024));
        Assert.Null(HostSystem.ReadOwnedFile(file, [MyUid], 8)); // too large
        Assert.Null(HostSystem.ReadOwnedFile(_directory, [MyUid], 1024)); // not a regular file
        Assert.Null(HostSystem.ReadOwnedFile(Path.Combine(_directory, "missing"), [MyUid], 1024));
    }

    [Fact]
    public async Task HostSystem_ReadOwnedFile_DoesNotBlockOnAFifo()
    {
        var fifo = Path.Combine(_directory, "fifo");
        using (var mkfifo = Process.Start("mkfifo", fifo))
        {
            await mkfifo.WaitForExitAsync();
        }

        var read = await Task.Run(() => HostSystem.ReadOwnedFile(fifo, [MyUid], 1024)).WaitAsync(TimeSpan.FromSeconds(5)); // TimeoutException if it blocked

        Assert.Null(read);
    }

    [Fact]
    public void HostSystem_UsersAndProcesses()
    {
        var me = HostSystem.GetUser(MyUid);

        Assert.NotNull(me);
        Assert.Equal(MyUid, me.Uid);
        Assert.Equal(MyUid, HostSystem.ProcessUid(Environment.ProcessId));
        Assert.Contains(Environment.ProcessId, HostSystem.ProcessIds());
        Assert.NotNull(HostSystem.ProcessEnvironment(Environment.ProcessId));
        Assert.Equal(SplitArguments, HostSystem.ParseArguments("a\0b c\0"u8));
        Assert.Equal("1", HostSystem.ParseEnvironment("A=1\0A=2\0junk\0B=x=y\0"u8)["A"]);
        Assert.Equal("x=y", HostSystem.ParseEnvironment("B=x=y\0"u8)["B"]);
    }

    // ------------------------------------------------------------------ configuration

    [Fact]
    public void AgentConfig_SaveAndLoad_RootOnlyPermissions()
    {
        var folder = Path.Combine(_directory, "etc");
        var path = Path.Combine(folder, "agent.json");

        new AgentConfig("https://support.example.com/", "token").Save(path);
        var loaded = AgentConfig.Load(path);

        Assert.Equal(new AgentConfig("https://support.example.com/", "token"), loaded);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));

        File.WriteAllText(path, """{"ServerUri":"http://evil.example.com","Token":null}""");
        Assert.Null(AgentConfig.Load(path)); // plain http only for localhost
    }

    [Fact]
    public void AgentInstaller_ParsesArguments()
    {
        var parsed = AgentInstaller.ParseInstallArguments(Server, alreadyEnrolled: false);

        Assert.NotNull(parsed);
        Assert.Equal("https://support.example.com/", parsed.Server.ToString());
        Assert.Equal("abc", parsed.Token);
        Assert.Null(AgentInstaller.ParseInstallArguments(NoToken, alreadyEnrolled: false));
        Assert.NotNull(AgentInstaller.ParseInstallArguments(NoToken, alreadyEnrolled: true));
        Assert.Null(AgentInstaller.ParseInstallArguments(Reenroll, alreadyEnrolled: true));
        Assert.Null(AgentInstaller.ParseInstallArguments(Ftp, alreadyEnrolled: false));
    }

    [Fact]
    public void AgentUpdater_InstallsOutsideTheServiceUnit()
    {
        var start = AgentUpdater.InstallCommand(AgentPaths.StagedUpdate, 1234);

        Assert.Equal("systemd-run", start.FileName);
        Assert.Contains("--unit=mkits-agent-update-1234", start.ArgumentList);
        Assert.Contains("--collect", start.ArgumentList);
        Assert.Equal("/usr/bin/apt-get", start.ArgumentList[4]);
        Assert.Equal(AgentPaths.StagedUpdate, start.ArgumentList[^1]); // absolute path: apt installs the local file
        Assert.Equal("linux-x64-deb", AgentUpdater.ChannelFor(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("linux-arm64-deb", AgentUpdater.ChannelFor(System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Fact]
    public void DuplexStream_ReadsFromOneAndWritesToTheOther()
    {
        byte[] sent = [1, 2, 3];
        byte[] received = [4, 5];
        using var input = new MemoryStream(sent);
        using var output = new MemoryStream();
        using var stream = new DuplexStream(input, output);
        var buffer = new byte[3];

        stream.ReadExactly(buffer);
        stream.Write(received);

        Assert.Equal(sent, buffer);
        Assert.Equal(received, output.ToArray());
        Assert.False(stream.CanSeek);
    }
}
