---
title: "Compliance Controls: HIPAA, SOX and PCI DSS"
description: "Which HIPAA, SOX and PCI DSS requirements BifrostQL's shipped controls meet, the option that sets each one, and the gaps the deployment has to close itself, each with its owner."
---

This page maps the access, authentication and audit requirements of HIPAA (45 CFR
164.312), SOX IT general controls and PCI DSS v4.0 onto what BifrostQL ships. Each
**supported** row names the option that controls it and the code that enforces it,
with the number of production call sites that reach the guard. Each **gap** row names
who has to close it.

Nothing here makes a deployment compliant on its own. An assessor looks at the
whole system: the database, the host, the network and the processes around them.
BifrostQL covers the parts that live in the API layer.

## Local-auth sessions and accounts

These controls apply to the local DB-backed login (`AddBifrostLocalAuth`). Sessions
signed in through OIDC are owned by the identity provider; see [Gaps](#gaps).

| Requirement | Control | Option (default) | Enforced by |
|---|---|---|---|
| PCI 8.2.8, HIPAA 164.312(a)(2)(iii) automatic logoff | A session idle for the timeout is revoked server-side. | `SessionIdleTimeout` (15 min) | `LocalSessionManager.CheckAsync`, called from `LocalAuthEndpoint.RevalidateSessionAsync` (3 call sites): the cookie `OnValidatePrincipal` hook on every HTTP request, the binary WebSocket (`/bifrost-ws`) on every message, and the `/_chat` stream before each tool call and before the reply is stored. A session that ends closes the WebSocket with PolicyViolation and ends the chat stream with a `session-ended` error. |
| Absolute session limit | A session ends a fixed time after sign-in, active or not. | `SessionLifetime` (8 h) | The `ExpiresAt` check in `CheckAsync`, plus the cookie expiry. |
| PCI 8.2.6 / 8.2.2 terminate access | Logout revokes the server session, not just the browser cookie. Password change, disable, delete, a role, tenant or household change, and any change to the second factor (enable, disable, new recovery codes, administrator reset) revoke every session of the user. An administrator can revoke all of a user's sessions. | `SessionRevalidationInterval` (1 min), `DisabledColumn`, `AdminRole` | `LocalSessionManager.RevokeAllAsync` (6 call sites: revalidation, password change, administrator revoke, administrator MFA reset, MFA disable, and the shared re-issue after MFA enable or recovery-code regeneration), `LocalAuthAdministration.RevokeAllSessionsAsync`. |
| Concurrent sessions | A sign-in past the cap revokes the user's oldest sessions. | `MaxConcurrentSessions` (no cap) | `LocalSessionManager.IssueAsync`. |
| PCI 8.3.4 lockout | 10 consecutive failures lock the account for 30 minutes, or until an administrator unlocks it. Wrong or replayed second-factor codes count toward the same counter as wrong passwords. A locked account answers exactly like a wrong password at login. Separate from the per-IP and per-account rate windows. A lapsed lock is cleared only while it is still the lapsed one, and a successful check never clears a lock taken after its own lockout check. For an enrolled user the count clears only when the second factor passes, never on the password alone. | `LockoutThreshold` (10), `LockoutDuration` (30 min; null = until unlocked) | `LocalAuthEndpoint.RecordFailedAttemptAsync` (5 call sites: login, password change, the password change's second factor, MFA login step two, and the signed-in MFA endpoints' shared refusal). `LocalAuthEndpoint.RefuseLockedAsync` (3 call sites: login, password change, MFA login step two); the signed-in MFA endpoints check the lock in their own `RefuseLockedAsync` (2 call sites: enrollment confirm, and the shared front of disable and recovery-code regeneration). `LocalAuthEndpoint.RefuseLockedSinceCheckAsync` (5 call sites: login for a user without a second factor, password change after its factor, MFA login step two, enrollment confirm, and the shared front of disable and regeneration), over the store's conditional `ClearExpiredLockoutAsync` and `ClearFailuresUnlessLockedAsync`. |
| Online-guessing throttle | Password attempts are counted per source IP and per account before the hash runs. Second-factor codes have their own windows, counted before the code is checked. A refusal answers 429 and is audited as a failed attempt with reason `rate-limited`. | `MaxLoginAttemptsPerSource` (20), `MaxLoginAttemptsPerAccount` (10), `MaxMfaAttemptsPerSource` (20), `MaxMfaAttemptsPerAccount` (10), `LoginAttemptWindow` (5 min) | `LocalLoginRateLimiter.TryLogin` (2 call sites: login and password change). `LocalMfaRateLimiter.TryAttempt` through `LocalMfaEndpoint.Admit` (3 call sites: login step two, enrollment confirm, and the shared front of disable and regeneration). The password change's factor rides on its `TryLogin` admission. |
| PCI 8.3.6 password strength | At least 12 characters, with a letter and a digit. | `MinPasswordLength` (12), `PasswordRequiresLetterAndDigit` (true), `MaxPasswordLength` (256) | `PasswordPolicyViolationAsync` on the password-change endpoint. |
| PCI 8.3.7 password reuse | Not the current password or any of the 4 before it. | `PasswordHistoryCount` (4) | Same; hashes kept in `_bifrost_auth_password_history`. |
| PCI 8.3.9 rotation | A password older than the limit cannot sign in until it is changed. | `MaxPasswordAge` (off) | `PasswordExpiredAsync` in the login handler. |
| PCI 8.3.2 credential storage | PBKDF2-HMAC-SHA512, 210,000 iterations (the OWASP Password Storage Cheat Sheet figure). Older hashes are upgraded at the next successful password check of an account that is not locked (for an enrolled user, at the password step, before the challenge is issued); the upgrade is conditional on the hash just verified and keeps the user's other sessions. An unknown login, or a stored hash below the configured count, spends the configured count's PBKDF2 work, so response time does not tell them apart. A wrong password returns before the second-factor row is read, so it answers and costs the same whether or not the account has a second factor. Below 210,000 fails at startup. | `PasswordHashIterations` (210,000) | `LocalAuthOptions.CreatePasswordHasher`. |
| PCI 8.4.1 / 8.4.2 MFA for all access, HIPAA 164.312(d) person or entity authentication, SOX ITGC logical access | A TOTP (RFC 6238) second factor. When the password verifies for an enrolled user, `/auth/login` returns `mfa-required` and a challenge ticket, and no session exists until `{MfaPath}/verify` accepts a code or recovery code. With `RequireMfa`, a user not yet enrolled gets an enrollment-only session: it is served on enroll, confirm and logout only. Every other HTTP request runs anonymous, the binary WebSocket closes with PolicyViolation at the next message, and a `/_chat` stream ends with `session-ended` before its next tool call. A password-only session of a user the requirement covers is revoked at its next check. | `RequireMfa` (**true**), `RequireMfaForRoles` (empty), `MfaKeyRef` (none; required while `RequireMfa` is on, else startup fails) | The `GetMfaAsync` branch in `LocalAuthEndpoint.HandleLoginAsync` (1 call site). `LocalAuthEndpoint.SignInSessionAsync` is the only creator of a local session cookie (4 call sites: password-only login, enrollment-only login, login step two, and the shared re-issue after enable or regeneration); for an enrolled user only `HandleVerifyAsync` reaches it. `LocalAuthEndpoint.RevalidateSessionAsync` returns the `EnrollmentOnly` verdict and applies the requirement revoke; its 3 call sites (the cookie hook, the binary WebSocket per message, the chat stream per tool call) each refuse that verdict. |
| PCI 8.5.1 MFA not replayable or bypassable | A code is accepted only for a time step above the last one accepted, written by compare-and-swap with the row version, so one code cannot pass twice even in concurrent requests. ±1 step of clock drift. Codes compare in constant time. The challenge ticket is single use, lives 5 minutes, is spent after 5 wrong codes, and is bound to the user and the credential stamp (a password change voids it). An enrolled user whose MFA key is removed from config is refused (403 `mfa_unavailable`), never let through on the password. The password change also needs the second factor. | `MfaChallengeLifetime` (5 min), `MaxMfaChallengeAttempts` (5), `MfaTotpAlgorithm` / `MfaTotpDigits` / `MfaTotpPeriodSeconds` (SHA-1 / 6 / 30 s) | `LocalMfaService.VerifyAsync` (4 call sites: login step two, enrollment confirm, the shared front of disable and regeneration, and the password change), `LocalMfaService.SpendChallengeAsync`. |
| MFA secret storage, PCI 3.5 / 8.3.2 | 32-byte CSPRNG secret, sealed with field encryption (AES-GCM under the data key of `MfaKeyRef`, AAD bound to the user id). Ten 80-bit recovery codes, shown once, stored as salted SHA-256, single use. Replacing them needs the current factor. | `MfaKeyRef`, an `IRootKeyProvider` and a durable `IDataEncryptionKeyStore` | `LocalMfaService`; rows in `_bifrost_auth_mfa`. |
| Lost authenticator | An administrator removes a user's second factor, audited with the administrator as actor; every session of the user is revoked. | `AdminRole` | `POST {AdminPath}/reset-mfa`, `LocalAuthAdministration.ResetMfaAsync`. |
| PCI 8.2.1, HIPAA 164.312(a)(2)(i) unique user id | Every session carries the app-user's stable id. | `IdColumn` | `AppIdentity.Id`. |

Session ids are 256-bit random values. The cookie carries the id; the database keeps
only its SHA-256. The state tables (`_bifrost_auth_sessions`, `_bifrost_auth_lockouts`,
`_bifrost_auth_password_history`, `_bifrost_auth_audit`, `_bifrost_auth_mfa`,
`_bifrost_auth_mfa_challenges`) live beside the app-user table. Challenge tickets, like
session ids, are stored only as their SHA-256.
They are created on first use and are never part of a served schema, whatever their
metadata says (`BifrostSystemTables.IsAlwaysHidden`). The gap is `_rawQuery`: it runs the
caller's own SQL, so with `raw-sql: enabled` a caller holding the `raw-sql-role` (default
`bifrost-raw-sql`) can `SELECT` these tables by name. Leave `raw-sql` disabled on a model whose
database holds them, or grant the role only to operators who may read session and audit rows.

## Audit trail

| Requirement | Control | Where it goes |
|---|---|---|
| PCI 10.2.1.4 invalid access attempts, 10.2.1.5 credential changes, 10.2.1.2 administrator actions; HIPAA 164.312(b) audit controls; SOX access logging | Login success and failure (including rate-limited refusals), logout, account locked, unlocked, password changed or rejected (including rate-limited refusals), session revoked, privilege changed; MFA enrollment started, enabled, disabled, challenge issued, verify success and failure (wrong, replayed, unusable ticket), recovery code used, recovery codes regenerated, administrator reset. | `_bifrost_auth_audit`, the `BifrostQL.Audit.Authentication` log category, and every registered `IAuthAuditSink`. |
| PCI 10.2.2 record content | Each event records time (UTC), kind, outcome, subject user id, acting administrator, source IP and a fixed-vocabulary reason. It never records a password, hash, session id, cookie, the login name typed, a TOTP secret, a code, a recovery code or a challenge ticket. | `AuthAuditEvent` |
| HIPAA 164.312(c)(1) integrity, SOX change trail | Who changed which column from what to what, in the same transaction as the write. | [Change history](/BifrostQL/guides/change-history/) |
| Record attribution | Created-by and updated-by columns filled from the caller's identity. | `AuditMutationTransformer` |

An audit write that fails fails the request: an unrecorded event is an error, not a
warning.

## Data protection

| Requirement | Control | Docs |
|---|---|---|
| HIPAA 164.312(a)(2)(iv), PCI 3.5 encryption of stored data | Column-level encryption with masking for roles that may not read plaintext. | [Field encryption](/BifrostQL/concepts/field-encryption/) |
| PCI 3.6 / 3.7 key management | Data-encryption-key rotation with an online re-encryption sweep. | [Rotating keys](/BifrostQL/guides/field-encryption/) |
| HIPAA 164.312(a)(1), PCI 7 least privilege | Row and column policies; tenant isolation. | [Authorization](/BifrostQL/guides/authorization/) |
| Retention and erasure | Scheduled purge policies; tombstoning of the history trail. | [Retention](/BifrostQL/guides/retention/) |
| SOX segregation of duties | A write can require a second person's approval. | [Approval workflows](/BifrostQL/guides/approval-workflows/) |

## Gaps

| Gap | Requirement | Owner | Notes |
|---|---|---|---|
| MFA for OIDC users | PCI 8.4.2, HIPAA 164.312(d) for OIDC users | Identity provider | Local auth enforces its own TOTP factor (above). An OIDC sign-in is trusted as the provider authenticated it; configure MFA there. |
| MFA attempt windows across nodes | PCI 8.3.4 / 8.4.2 under scale-out | Host | The code rate windows, like the password ones, are per process. The lockout counter and replay record are in the database and hold across nodes. |
| Phishing-resistant MFA | PCI 8.4 guidance, NIST AAL3 | Host | TOTP is not phishing-resistant. WebAuthn is not shipped; front local auth with an OIDC provider that offers it where that matters. |
| OIDC session controls | PCI 8.2.8, 8.3.4 for OIDC users | Identity provider | Idle timeout, lockout, revocation and audit above cover local auth only. An OIDC cookie is checked against the provider's own session rules. |
| Read-access audit | PCI 10.2.1.1 (access to cardholder data) | Host | No shipped observer records reads. `IQueryObserver` can, but observer exceptions are logged and do not abort the query, so it cannot fail closed. |
| Inactive-account disable | PCI 8.2.6 (90 days) | Host | `DisabledColumn` honours a flag; nothing sets it on inactivity. A scheduled job over `_bifrost_auth_audit` login events can. |
| Encryption at rest (whole database) | HIPAA 164.312(a)(2)(iv), PCI 3.5 | Database operator | TDE or volume encryption. Field encryption covers chosen columns only. |
| Root-key rotation | PCI 3.7.4 | Host | No shipped re-wrap API; see [Rotating keys](/BifrostQL/guides/field-encryption/). The ASP.NET Core Data Protection keys that sign session cookies are the host's to persist and rotate. |
| Transport encryption | HIPAA 164.312(e)(1), PCI 4.2 | Host | Terminate TLS in Kestrel or a proxy, and configure TLS on every wire adapter you enable. gRPC serves cleartext h2c unless `RequireTls` is set, with a startup warning. |
| Audit and log retention, tamper evidence | PCI 10.5.1 (12 months), 10.3 | Operator | `_bifrost_auth_audit` is append-only from BifrostQL but has no purge or hash chain. Ship the log category to a SIEM with write-once retention. |
| Network segmentation | PCI 1.x | Operator | Every non-HTTP listener binds loopback by default; widening is the operator's decision (see the posture table in the protocol-adapter guide). |

### MFA design

Local auth follows the earlier design notes with one change. The notes proposed a
"password-verified" session row with no privileges after the password. The shipped login
creates no session at all for an enrolled user: it returns a 5-minute, single-use challenge
ticket, stored only as its hash and bound to the user and the credential stamp, and only
`POST {MfaPath}/verify` with a code turns it into a session. Nothing that runs on a session
can mistake the half-way state for a signed-in user. The enrollment-only session for a user
who has not enrolled yet is the one place the notes' unprivileged row survives, and
`LocalAuthEndpoint.RevalidateSessionAsync`, the one session check behind the cookie hook,
the binary WebSocket and the chat stream, keeps it off every path but enrollment and logout.

`RequireMfa` is on by default (deny, with an explicit opt-out). A host that sets no
`MfaKeyRef` fails at startup with a message naming both ways out: configure the key, or
set `RequireMfa = false`. See [Authentication](/BifrostQL/guides/authentication/#multi-factor-authentication).
