# Session lifecycle

> Status: **implemented on the server (Phase 5)**: `Features/Sessions` (persisted state, codes) and `Features/Relay` (live state machine, timeouts). Timeouts are defaults and configurable (`Mkits:Sessions:*`).

## 1. Session kinds

| Kind | Created by | Agent | Consent |
|---|---|---|---|
| `AdHoc` | User/Admin in the web UI | portable client started by the customer | always *ask* + banner (ADR-0014) |
| `Permanent` | User/Admin opening an online device | installed agent | device group policy: *ask*, *notify* or *silent* |

Guests (unauthenticated customers) never create sessions themselves: they redeem a code created by a supporter (ADR-0004).
Authenticated Users can also act as customers – they use the same portal flow.

## 2. Session codes

- **8 decimal digits**, displayed as `1234 5678`, generated with `RandomNumberGenerator`; length configurable (6–12).
  Rationale: 10^8 combinations, ≤ 50 active codes and 5 failures per IP per week (IP ban) make guessing impractical while staying phone-friendly.
- Stored only as **SHA-256 hash** (codes are short-lived, high-entropy relative to the attempt budget; no plaintext in the database).
- Valid for **10 minutes** until redeemed by an agent; **single use**: the code is consumed when an agent authenticates with it.
- Code validation on the portal (download step) does **not** consume the code but counts failures for the IP ban service.
- The supporter can revoke a code at any time.

## 3. State machine

```
                 create (supporter)                       agent redeems code /
 ┌────────────────────────────┐                          permanent device selected
 │                            ▼                                     │
 │                   ┌──────────────────┐   code expires   ┌────────┴─────────┐
 │                   │ WaitingForAgent  ├─────────────────►│      Ended       │
 │                   └────────┬─────────┘                  └──────────────────┘
 │                            │ agent connected                     ▲  ▲  ▲
 │                            ▼                                     │  │  │
 │                   ┌──────────────────┐  no viewer for 15 min     │  │  │
 │                   │ WaitingForViewer ├───────────────────────────┘  │  │
 │                   └────────┬─────────┘                              │  │
 │                            │ viewer joins                           │  │
 │                            ▼                                        │  │
 │                   ┌──────────────────┐  denied / 2 min timeout      │  │
 │                   │ AwaitingConsent  ├──────────────────────────────┘  │
 │                   └────────┬─────────┘  (skipped for notify/silent)    │
 │                            │ granted + key exchange done               │
 │                            ▼                                           │
 │                   ┌──────────────────┐  EndSession by any side         │
 │                   │      Active      ├─────────────────────────────────┤
 │                   └──┬───────────▲───┘                                 │
 │       peer connection│lost       │reconnected within 60 s              │
 │                      ▼           │                                     │
 │                   ┌──────────────┴───┐  grace period (60 s) expired    │
 │                   │    Suspended     ├─────────────────────────────────┘
 │                   └──────────────────┘
```

| Transition | Audit event |
|---|---|
| created | `session.created` (supporter, kind, device) |
| code redeemed / agent connected | `session.agent_connected` (IP, machine info) |
| viewer joined / left | `session.viewer_joined` / `session.viewer_left` |
| consent granted / denied / timeout | `session.consent` (result) |
| active | `session.started` (recording yes/no) |
| suspended / resumed | `session.suspended` / `session.resumed` |
| ended | `session.ended` (reason, duration, bytes relayed) |

## 4. Multiple supporters

A session may have several viewers. The creator is the **owner**; further supporters can be invited by the owner or join permanent devices they are authorized for. Each additional viewer triggers a new consent request when the policy is *ask*, and its own key exchange. Only one viewer controls input at a time (*control token*, requested/handed over explicitly); others watch.

## 5. Ad-hoc client delivery

The portal serves one generic client per OS. Server address and code reach the client without re-signing the binary (ADR-0019):

- **File name**: `MKITS-Support_<host>_<code>.exe` (Windows), e.g. `MKITS-Support_support.example.com_12345678.exe`. The client parses its own file name; browser suffixes like ` (1)` are tolerated.
- **Fallback**: if the name cannot be parsed, the client asks for server address and code.
- The client always shows the server address and the operator name (from `SessionAssigned`) in its consent dialog, so end users see who connects.

Linux: same scheme with an AppImage/tarball *(Phase 11)*. Android: APK + code entry in the app *(Phase 12)*.

## 6. Ending a session

A session ends when:

- the customer clicks *End session* in the agent UI (always available, cannot be disabled), or closes the ad-hoc client;
- the owner or an Admin ends it in the web UI;
- consent is denied or times out;
- the suspension grace period expires;
- the maximum session duration (default **8 h**) is reached;
- an Admin disables the device, group or user involved.

On end, the ad-hoc client removes its temporary service (if elevated) and exits; permanent agents return to idle presence.

## 7. Permanent device sessions

- A supporter opens an online device on `/devices` (requires a permission on the device's group; Admins have all). If the device is already in a session, the supporter **joins** it as an additional viewer; otherwise a new `Permanent` session is created and bound to the device's presence connection right away (`WaitingForAgent` is skipped).
- Consent follows the group policy (*ask* / *notify* / *silent*, ADR-0014). Consent decisions – including automatic ones (`policy_notify`, `policy_silent`) – are audited.
- A device session without any viewer ends after `DeviceSessionIdleTimeout` (default **2 min**, reason `no_viewer`); the device stays online.
- Disabling or removing a device ends its session (reason `device_disabled`) and closes its connection. Moving a device to another group takes effect for new sessions; permissions are checked whenever a viewer ticket is issued.
