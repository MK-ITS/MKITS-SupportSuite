# Supporter guide

For people who help customers with MKITS Remote Support: starting sessions, working in the viewer, and connecting to
installed devices. Administrators find setup and policies in the [administration guide](administration.md).

You need a user account on your organization's server (your admin creates it) and a current browser (Chrome, Edge,
Firefox or Safari). Sign in at your server's address, e.g. `https://support.example.com/account/login`.

## 1. Help a customer (ad-hoc session)

1. Open **Sessions** and click **New ad-hoc session**. Add a note if you like (ticket number, customer name) and click
   **Create session code**.
2. Tell the customer the 8-digit code, e.g. on the phone. It is valid for **10 minutes**, works **once**, and is shown
   to you only this one time. You can revoke a code you no longer need.
3. The customer opens your server's start page, enters the code and starts the support client (see the
   [customer guide](customer.md)). The client shows your name and your server's address and asks the customer for
   consent.
4. In **Sessions**, click **Open** next to the session; the viewer opens in a new tab. As soon as the customer has agreed, you see the screen.
5. **Compare the security code** shown in the viewer's top bar with the one in the customer's window, at least when the
   case is sensitive. If the codes are the same, the connection is end-to-end encrypted between the two of you – not
   even the server can read along. If they differ, end the session.
6. When you are done, click **End session**. The customer can also end it at any time; the client then closes and
   leaves nothing installed.

Several supporters can join the same session; the customer sees everybody who is connected.

## 2. Working in the viewer

| Tool | What it does |
|---|---|
| **Screen** | Click the screen to control it: mouse, wheel and keyboard go to the remote computer (keyboard layout of the remote side). Your mouse is visible to the customer. |
| **Monitor** | Choose the monitor if the remote computer has several. |
| **Picture quality** | *Low bandwidth* (10 frames/s) for slow connections, *Balanced* (15 frames/s, default), *Best quality* (30 frames/s, sharper). The viewer uses H.264 video where the browser and the remote computer support it, otherwise single images. |
| **1:1** | Shows the remote screen at its real size and lets you scroll; otherwise it is scaled to the window. |
| **Ctrl+Alt+Del, Win, Alt+Tab** | Key combinations your own computer would otherwise catch. Ctrl+Alt+Del needs administrator mode (ad-hoc) or an installed agent. |
| **Chat** | Text messages with the customer. |
| **Files** | *Send file* offers a file to the customer, who must accept it; it lands in their Downloads folder. Files the customer sends appear with *Save*. |
| **Clipboard** | *Send clipboard* puts your clipboard text on the remote computer, *Copy customer clipboard* takes theirs (the browser may ask for permission). |
| **Terminal** | A command line on the remote computer (PowerShell on Windows, the login shell on Linux) with the rights of the logged-on user. Several terminals can be open. |
| **Admin terminal** | A command line with administrator rights – SYSTEM on Windows, root on Linux. See below. |

The viewer remembers picture quality and *1:1* in your browser.

### Administrator rights

- **Ad-hoc sessions (Windows):** the customer can click **Restart with administrator rights** in their window and
  confirm the Windows prompt (UAC). Then you also see and use security prompts, the lock screen and Ctrl+Alt+Del, and
  the *Admin terminal* works. The mode ends with the session.
- **Installed devices:** the *Admin terminal* needs the permission **Admin shell** for the device's group (your admin
  grants it). Without it the request is refused – you never get a weaker shell instead without noticing.

## 3. Installed devices (permanent access)

Computers with the installed agent appear under **Devices**, as far as your groups allow.

1. Pick the device and click **Connect** (it must be online). If a colleague is already connected, **Join session** adds you to that session.
2. Depending on the group's policy the session starts right away, the logged-on user is only informed, or the user is
   asked to agree first (as with an ad-hoc session).
3. You work in the same viewer as above. What you may do – view, control, files, clipboard, terminal, admin terminal –
   depends on your permissions for that group; buttons for things you may not do have no effect.

Nobody logged on? On Windows you see the login screen and can sign in; on Linux you see the login screen if it runs
on X11. Admin terminals work without anybody being logged on.

## 4. Good practice

- Give codes only to the customer you are talking to, and only when they need it.
- Compare the security code before you handle passwords or sensitive data.
- Tell the customer before you open files or terminals, and end the session as soon as you are done.
- Everything that matters (sessions, consents, file transfers, terminals) is recorded in the audit log.

## 5. Troubleshooting

| Problem | What to do |
|---|---|
| The customer's code is "not valid" or "expired" | Create a new code – codes work once and for 10 minutes. |
| The viewer stays on "Connecting …" | The customer has not started the client or has not agreed yet; ask them. Reload the page if the network was interrupted. |
| Picture is choppy | Choose *Low bandwidth*; close other tabs with video. |
| *Admin terminal* "not available" | Ad-hoc: the customer has to restart with administrator rights. Installed device: ask your admin for the *Admin shell* permission. |
| Clipboard buttons do nothing | Your browser blocked clipboard access; allow it for the site. |
