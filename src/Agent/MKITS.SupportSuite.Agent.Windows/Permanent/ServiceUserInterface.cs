// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Runtime.InteropServices;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Agent.Windows.Platform;
using MKITS.SupportSuite.Agent.Windows.UI;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>
/// What the user at the device sees while the agent runs as a service (ADR-0022): Windows message boxes in the
/// console session (<c>WTSSendMessage</c>) – a Yes/No question under the <i>ask</i> policy, a notice under
/// <i>notify</i>, nothing under <i>silent</i>. If nobody is logged on at the console, nobody can answer: <i>ask</i>
/// requests are declined. The session is released (helper stopped) when it ends.
/// </summary>
internal sealed partial class ServiceUserInterface(OnDemandHelper helper) : IAgentUserInterface
{
    private const int MbYesNo = 0x4;
    private const int MbOk = 0x0;
    private const int MbIconQuestion = 0x20;
    private const int MbIconInformation = 0x40;
    private const int MbTopMost = 0x40000;
    private const int MbSetForeground = 0x10000;
    private const int IdYes = 6;
    private const int WtsUserName = 5;

    /// <summary>Shorter than the server's consent timeout (2 min), so a late click never races the timeout.</summary>
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(100);

    private ConsentPolicy _policy = ConsentPolicy.Ask;
    private string _operator = string.Empty;
    private IReadOnlyList<string> _announced = [];

    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return Task.Run(() =>
        {
            var session = ConsoleSessionLauncher.ActiveSessionId;
            if (session == ConsoleSessionLauncher.NoSession || !UserLoggedOn(session))
            {
                AgentLog.Write($"consent for {prompt.SupporterDisplayName} declined: nobody is logged on at the console");
                return false;
            }

            var text = UiText.Format("Service_Consent_Text", prompt.SupporterDisplayName, prompt.ServerHost, PermissionText(prompt.Permissions));
            var granted = Show(session, UiText.Get("Consent_Title"), text, MbYesNo | MbIconQuestion | MbTopMost | MbSetForeground, ConsentTimeout, wait: true) == IdYes;
            AgentLog.Write($"consent for {prompt.SupporterDisplayName}: {(granted ? "granted" : "denied")}");
            return granted;
        }, cancellationToken);
    }

    public void OnSessionAssigned(string operatorName, ConsentPolicy policy)
    {
        _policy = policy;
        _operator = operatorName;
        _announced = [];
        AgentLog.Write($"session assigned (operator {operatorName}, policy {policy})");
    }

    public void OnStatusChanged(AgentStatus status, string? detail)
    {
        AgentLog.Write(detail is null ? $"status {status}" : $"status {status} ({detail})");
        if (status is AgentStatus.Idle or AgentStatus.Reconnecting or AgentStatus.Ended)
        {
            helper.Release(); // no session (or no connection): no SYSTEM process in the user's session
        }
    }

    public void OnSupportersChanged(IReadOnlyList<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(displayNames);

        // Notify policy: tell the user about every supporter that joins (once per supporter and session).
        var joined = displayNames.Except(_announced, StringComparer.Ordinal).ToList();
        _announced = displayNames.ToList();
        if (_policy != ConsentPolicy.Notify || joined.Count == 0)
        {
            return;
        }

        var session = ConsoleSessionLauncher.ActiveSessionId;
        if (session != ConsoleSessionLauncher.NoSession && UserLoggedOn(session))
        {
            var text = UiText.Format("Service_Notify_Text", string.Join(", ", joined), _operator);
            _ = Task.Run(() => Show(session, UiText.Get("App_Title"), text, MbOk | MbIconInformation | MbTopMost, TimeSpan.FromMinutes(1), wait: false));
        }
    }

    public void OnSecurityCode(string supporterDisplayName, string securityCode) =>
        AgentLog.Write($"key exchange with {supporterDisplayName} completed");

    private static string PermissionText(IReadOnlyList<string> permissions) =>
        string.Join(", ", permissions.Select(p => UiText.Get("Permission_" + p)));

    private static int Show(uint session, string title, string text, int style, TimeSpan timeout, bool wait)
    {
        if (!WTSSendMessage(0, session, title, title.Length * 2, text, text.Length * 2, style, (int)timeout.TotalSeconds, out var response, wait))
        {
            AgentLog.Write($"WTSSendMessage failed ({Marshal.GetLastPInvokeError()})");
            return 0;
        }

        return response;
    }

    private static bool UserLoggedOn(uint session)
    {
        if (!WTSQuerySessionInformation(0, session, WtsUserName, out var buffer, out _))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSSendMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSSendMessage(
        nint server, uint sessionId, string title, int titleLength, string message, int messageLength, int style, int timeoutSeconds,
        out int response, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out int bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
