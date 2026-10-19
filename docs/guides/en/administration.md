# Administration guide

> 🇩🇪 German version: [../de/administration.md](../de/administration.md)

For operators and Admins of an MKITS-SupportSuite server. Installation: [installation.md](installation.md).

> **Project status (0.1.0-dev):** this guide describes the target behavior so operators can plan ahead. The status column in section 1 shows what is available today.

## 1. Feature status

| Area | Status |
|---|---|
| Server container, nginx setup, health/version endpoints, configuration validation | ✅ available |
| Security headers, trusted reverse proxies, data directory, localization (EN/DE) | ✅ available |
| First-run setup, login, users, roles, TOTP 2FA, profile/language | ✅ available |
| IP bans, allow/deny lists, audit log with integrity check, e-mail notifications (admin UI) | ✅ available |
| Ad-hoc session codes (create, list, revoke, expiry) | ✅ available |
| Customer portal, consent, relay | 🔜 Phases 5–6 |
| Windows agent, browser viewer (chat, files, clipboard, shell) | 🔜 Phases 7–9 (milestone M1) |
| Device groups, permanent access, installers, auto-update | 🔜 Phase 10 (M2) |
| Linux agent (X11 + Wayland) | 🔜 Phase 11 (M3) |
| Android agent | 🔜 Phase 12 (M4) |
| Desktop viewer, session recording | 🔜 Phase 13 (M5) |

## 2. Concepts

**Roles**

| Role | Can |
|---|---|
| Guest (not logged in) | open the customer portal, enter a session code, download the client and receive support |
| User | everything a Guest can, plus log in, create ad-hoc sessions and connect as supporter to devices of groups they are permitted for |
| Admin | everything a User can, plus manage users, device groups, permanent devices, policies, IP bans, settings, audit log and recordings |

**Session kinds**

- **Ad-hoc:** a supporter creates a session and tells the customer an 8-digit code. The customer enters it on the portal, runs the downloaded client and approves the connection. Nothing stays installed.
- **Permanent:** an agent is installed on a device and enrolled into a **device group**. Authorized supporters can connect at any time according to the group's **consent policy**.

**Consent policies** (per device group): *Ask* (end user must approve), *Notify* (banner only), *Silent* (no prompt – use only where legally permitted, e.g. unattended servers). Ad-hoc sessions always use *Ask*. A visible banner and an *End session* button are always present on the end device.

**Security model in one sentence:** all traffic runs over HTTPS/WSS 443 through your server, session content is end-to-end encrypted between agent and viewer, so the server cannot read it – except for sessions you explicitly record (announced to all participants).

## 3. First login and two-factor authentication

1. Create the first Admin with the setup token (installation guide, section 7).
2. Scan the QR code with an authenticator app (e.g. Microsoft Authenticator, Aegis, 2FAS) and store the **recovery codes** safely.
3. Choose your interface language (English/German) in the profile. Without a profile setting, the browser language is used.

## 4. Users

*Administration → Users*

- **Create user:** user name, e-mail, display name (shown to customers in the consent dialog), role, language. The server generates an initial password, which is shown **once** – pass it on securely; the user changes it under *My account*.
- **Reset password:** generates a new password (shown once) and unlocks the account.
- **Reset 2FA:** removes the authenticator; Admins are forced to set it up again at the next login. All existing logins of that user are signed out within a minute.
- **Make admin / make user:** changes the role.
- You cannot disable or demote yourself, and the last active Admin is protected.
- **Disable user:** blocks login immediately and ends the user's active sessions. Disabled users keep their audit history.
- **2FA policy:** mandatory for Admins (admin pages are not accessible without it); Users can enable it under *My account*. Making it mandatory for Users is planned (`Mkits__Security__RequireTwoFactorForUsers`).
- **Passwords:** at least 12 characters, no composition rules (long passphrases are better). After 10 wrong attempts an account is locked for 15 minutes (in addition to IP bans).

## 5. Devices and permanent access *(planned)*

### 5.1 Device groups

*Administration → Device groups* – typically one group per customer or site.

| Setting | Meaning |
|---|---|
| Consent policy | Ask / Notify / Silent |
| Recording policy | Off / Optional (supporter decides) / Required |
| Require enrollment approval | new devices stay *pending* until an Admin approves them |
| Permissions | per User: View, Control, Files, Clipboard, Shell |

### 5.2 Enrolling devices

Create an **enrollment token** in the group (validity, maximum number of uses). Then:

- **Windows:** download the MSI from the group page and install it, e.g. via GPO/Intune:
  `msiexec /i MKITS-Agent.msi /qn MKITS_SERVER=https://support.example.com MKITS_TOKEN=<token>`
- **Linux (Debian/Ubuntu):** `sudo apt install ./mkits-agent_<version>_amd64.deb && sudo mkits-agent enroll --server https://support.example.com --token <token>`
- **Android:** install the APK from the portal, open the app, scan the enrollment QR code, enable the accessibility service when asked.

Tokens are shown once, stored only as hashes and can be revoked. Revoke tokens after a rollout.

### 5.3 Managing devices

The device list shows online status, OS, agent version, last seen and last IP. Actions: rename, move to another group, disable (blocks connections), remove (the agent must be re-enrolled). Agents update themselves automatically from the server (signed updates).

## 6. Ad-hoc support workflow *(partly available)*

Step 1 is available (menu *Sessions*); the remaining steps follow with the portal, client and viewer.

1. *Sessions → New ad-hoc session*, optionally enter a note (e.g. ticket number) and click *Create session code*. The code is valid for 10 minutes, can be used once and is shown only once. You can revoke codes you no longer need.
2. Tell the customer to open `https://support.example.com` and enter the code.
3. The customer downloads and runs the client; it shows your server address and your display name and asks for consent.
4. Open the session in your browser. Compare the 6-digit **security code** with the customer on the phone if the case is sensitive – it proves that nobody (not even a compromised server) sits in between.
5. Tools: monitor selection, Ctrl+Alt+Del, chat, file transfer, clipboard, remote shell, *Restart with administrator rights* (customer confirms the UAC prompt).
6. End the session. The client removes itself.

## 7. Security administration

### 7.1 IP bans

Failed logins, wrong 2FA codes, wrong session codes, invalid enrollment tokens and invalid device signatures are counted per IP address (IPv6 per /64 network). Defaults: **5 failures within 10 minutes → blocked for 7 days.**

*IP bans* (top navigation): view active bans, lift a ban, ban a network manually (duration in days, 0 = permanent), manage allow and deny entries. Entries from the configuration are shown read-only.

- **Allow list:** networks that are never banned (e.g. your office). Do not add networks you do not control.
- **Deny list:** networks that are always rejected.

Every ban and unban is written to the audit log; Admins can receive e-mail notifications (section 8).

### 7.2 Reverse proxy trust

The server only accepts client IP addresses from the proxies configured in `Mkits__ReverseProxy__*` (installation guide, section 8). Check after installation that the audit log shows real client IPs, not `172.30.80.1`.

### 7.3 Recommendations

- Keep the number of Admins small; use personal accounts only.
- Use *Silent* consent only for unattended servers and where legally permitted (inform users, works council).
- Review the audit log regularly and run the integrity check.
- Keep the host updated (`unattended-upgrades`) and update MKITS promptly.
- Back up the data volume daily; protect backups (they contain keys).

## 8. E-mail notifications

Configured via environment variables; *Settings* (top navigation) shows the effective values and sends a test e-mail to you:

```ini
Mkits__Smtp__Host=smtp.example.com
Mkits__Smtp__Port=587
Mkits__Smtp__Security=StartTls        # StartTls | SslOnConnect | None
Mkits__Smtp__UserName=mkits@example.com
Mkits__Smtp__Password=…               # better: Docker secret / .env with chmod 600
Mkits__Smtp__From=MKITS Support <mkits@example.com>
```

Notifications are sent to every active Admin with an e-mail address, in the Admin's UI language. Currently: automatic IP bans. Planned: new devices pending approval, failed integrity check, server update available. At most `Mkits__Smtp__MaxNotificationsPerHour` (default 20) e-mails per hour are sent, so an attack cannot flood your mailbox.

## 9. Audit log and recordings

- *Audit log* (top navigation): filter by event type and free text (IP, IDs, details), newest first, 50 per page. The log is hash-chained; *Verify integrity* detects modified or deleted entries. *(CSV/JSON export: planned.)*
- *Recordings* *(planned)*: play back, download or delete recordings. Playback is audited. Recordings are deleted automatically after the retention period.

## 10. Settings reference

Environment variables (`Section__Key`) override settings from the admin UI. ✅ = available today, 🔜 = planned (name may still change until the feature is released).

| Setting | Default | | Description |
|---|---|---|---|
| `Mkits__PublicBaseUrl` | – | ✅ | public URL of the server |
| `Mkits__DataDirectory` | `/data` | ✅ | data directory |
| `Mkits__ReverseProxy__KnownProxies__N` | – | ✅ | trusted proxy IPs |
| `Mkits__ReverseProxy__KnownNetworks__N` | – | ✅ | trusted proxy networks (CIDR) |
| `Mkits__ReverseProxy__ForwardLimit` | `1` | ✅ | proxy hops |
| `Mkits__IpBan__MaxFailures` | `5` | ✅ | failures before a ban |
| `Mkits__IpBan__Window` | `00:10:00` | ✅ | counting window |
| `Mkits__IpBan__BanDuration` | `7.00:00:00` | ✅ | ban length (d.hh:mm:ss) |
| `Mkits__IpBan__IPv6PrefixLength` | `64` | ✅ | IPv6 aggregation |
| `Mkits__IpBan__AllowList__N` / `DenyList__N` | – | ✅ | CIDR lists (in addition to the admin UI) |
| `Mkits__Sessions__CodeLength` | `8` | ✅ | digits of ad-hoc codes (6–12) |
| `Mkits__Sessions__CodeLifetime` | `00:10:00` | ✅ | validity of unused codes |
| `Mkits__Sessions__MaxActiveCodes` | `50` | ✅ | server-wide limit of open codes (with 8 digits at most 1000) |
| `Mkits__Sessions__MaxActiveCodesPerUser` | `10` | ✅ | open codes per supporter |
| `Mkits__Sessions__ConsentTimeout` | `00:02:00` | 🔜 | time for the end user to approve |
| `Mkits__Sessions__SuspendGracePeriod` | `00:01:00` | 🔜 | reconnect window after connection loss |
| `Mkits__Sessions__MaxDuration` | `08:00:00` | ✅ | maximum session length |
| `Mkits__Security__RequireTwoFactorForUsers` | `false` | 🔜 | 2FA also for Users |
| `Mkits__Smtp__Host`, `__Port` (587), `__Security` (StartTls), `__UserName`, `__Password`, `__From` | – | ✅ | e-mail (section 8); empty Host = disabled |
| `Mkits__Smtp__MaxNotificationsPerHour` | `20` | ✅ | flood protection |
| `Mkits__Retention__AuditLogDays` | `365` | 🔜 | audit log retention |
| `Mkits__Retention__RecordingDays` | `30` | 🔜 | recording retention |
| `Mkits__Retention__FailedAttemptDays` | `30` | 🔜 | failed-attempt records |

## 11. Data stored (privacy)

| Data | Purpose | Retention |
|---|---|---|
| User accounts (name, e-mail, password hash, 2FA secret) | login | until deleted |
| Devices (name, OS, agent version, last IP, last seen) | permanent access | until removed |
| Session metadata (who, when, which device, duration, IPs, consent result) | operation, audit | audit retention |
| Audit log | accountability | 365 days (configurable) |
| Failed attempts and IP bans | brute-force protection | 30 days / ban duration |
| Recordings (only if enabled, announced to participants) | documentation | 30 days (configurable) |

Session content (screen, keystrokes, files, chat, shell) of non-recorded sessions is end-to-end encrypted and **not** stored on the server. You are the data controller; inform your users and customers accordingly.

## 12. Monitoring and logs

- Health: `https://support.example.com/health` (`Healthy` / `Unhealthy`) – suitable for uptime monitoring.
- Version: `https://support.example.com/api/system/version`.
- Logs: `sudo docker compose -f /opt/mkits/docker-compose.yml logs -f server` (rotated by Docker: 5 × 20 MB).
- Logs never contain passwords, session codes, tokens or keys.
