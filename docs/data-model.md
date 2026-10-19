# Data model

> Status: **specification (Phase 2)**. Implemented with EF Core on SQLite (ADR-0006), starting in Phase 3/4.
> Conventions: primary keys are `Guid` (v7, time-ordered) unless noted; all timestamps are UTC (`DateTimeOffset`); secrets are stored as hashes only.

## Entity overview

```
 User ─┬─< GroupPermission >── DeviceGroup ──< Device
       │                           │
       │                           └──< EnrollmentToken
       ├─< SupportSession >── Device (optional)
       │        ├──< SessionParticipant >── User
       │        └──< Recording
       └─< AuditEvent (actor)

 IpBan        IpListEntry        Setting        (independent)
```

## Entities

### User *(ASP.NET Core Identity: `IdentityUser<Guid>`)*

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| UserName, Email | string | unique, normalized |
| DisplayName | string | shown to customers in consent dialogs |
| Role | enum `User`, `Admin` | stored as Identity role |
| UiCulture | string | `en` or `de`; null = browser language |
| PasswordHash, SecurityStamp, TwoFactorEnabled, AuthenticatorKey, RecoveryCodes | Identity | authenticator key protected with Data Protection |
| IsDisabled | bool | disabled users cannot log in; active sessions are ended |
| CreatedAt, LastLoginAt | DateTimeOffset | |

### DeviceGroup

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| Name | string | unique, e.g. customer name |
| Description | string? | |
| ConsentPolicy | enum `Ask`, `Notify`, `Silent` | ADR-0014 |
| RecordingPolicy | enum `Off`, `Optional`, `Required` | ADR-0018 |
| RequireEnrollmentApproval | bool | new devices wait for Admin approval |

### GroupPermission

| Field | Type | Notes |
|---|---|---|
| UserId, GroupId | Guid | composite key |
| Permissions | flags `View`, `Control`, `Files`, `Shell`, `Clipboard` | Admins implicitly have all |

### Device

| Field | Type | Notes |
|---|---|---|
| Id | Guid | also the device id in the protocol |
| GroupId | Guid | |
| Name | string | defaults to machine name, editable |
| Platform | enum `Windows`, `Linux`, `Android` | |
| OsName, OsVersion, AgentVersion | string | reported by the agent |
| PublicKey | byte[] | ECDSA P-256 SubjectPublicKeyInfo |
| Status | enum `PendingApproval`, `Active`, `Disabled` | |
| EnrolledAt, LastSeenAt | DateTimeOffset | |
| LastIp | string | |
| Notes | string? | |

Online/offline is **not** stored – it is derived from the live connection registry.

### EnrollmentToken

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| GroupId | Guid | |
| TokenHash | byte[] | SHA-256 of the 256-bit token |
| ExpiresAt | DateTimeOffset | |
| MaxUses, Uses | int | |
| CreatedByUserId | Guid | |
| RevokedAt | DateTimeOffset? | |

### SupportSession *(table `Sessions`, implemented in Phase 5A)*

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| Kind | enum `AdHoc`, `Permanent` | |
| CodeHash | byte[]? | ad-hoc only, SHA-256 of the session code |
| CodeExpiresAt | DateTimeOffset? | |
| OwnerUserId | Guid | supporter who created it |
| DeviceId | Guid? | permanent only |
| CustomerLabel | string? | free text from the supporter, e.g. ticket number |
| Status | enum (see [session-lifecycle.md](session-lifecycle.md)) | |
| AgentMachineName, AgentIp | string? | |
| CreatedAt, StartedAt, EndedAt | DateTimeOffset? | |
| EndReason | string? | |
| Recorded | bool | |
| BytesRelayed | long | |

### SessionParticipant

| Field | Type | Notes |
|---|---|---|
| SessionId, UserId | Guid | |
| JoinedAt, LeftAt | DateTimeOffset | |
| ConsentResult | enum `Granted`, `Denied`, `NotRequired`, `TimedOut` | |

### Recording

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| SessionId | Guid | |
| FilePath | string | relative to `/data/recordings` |
| SizeBytes | long | |
| KeyId | string | recording key used |
| CreatedAt, ExpiresAt | DateTimeOffset | retention |

### AuditEvent *(append-only)*

| Field | Type | Notes |
|---|---|---|
| Id | long | sequential |
| Timestamp | DateTimeOffset | |
| ActorType | enum `User`, `Guest`, `Device`, `System` | |
| ActorId | string? | |
| Ip | string? | |
| EventType | string | dotted identifier, e.g. `auth.login_failed`, `session.started`, `ipban.created` |
| TargetType, TargetId | string? | |
| Details | string (JSON) | never contains secrets |
| PreviousHash, Hash | byte[] | hash chain (security-concept T16) |

### IpBan

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| Network | string | CIDR, e.g. `203.0.113.7/32`, `2001:db8:1:2::/64` |
| Reason | string | e.g. `auto:login` or admin comment |
| CreatedAt, ExpiresAt | DateTimeOffset | `ExpiresAt` null = permanent |
| CreatedByUserId | Guid? | null = automatic |
| LiftedAt, LiftedByUserId | | manual unban |

### IpListEntry

| Field | Type | Notes |
|---|---|---|
| Id | Guid | |
| Network | string | CIDR |
| ListType | enum `Allow`, `Deny` | |
| Comment | string? | |

### Setting

Runtime settings editable in the admin UI (SMTP, branding, policies). Key/value, values as JSON. File/environment configuration always wins over database settings for keys that exist in both (so operators can lock settings).

| Field | Type |
|---|---|
| Key | string (PK), e.g. `Smtp`, `Branding`, `IpBan` |
| Value | string (JSON) |
| UpdatedAt, UpdatedByUserId | |
