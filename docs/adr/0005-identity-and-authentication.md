# ADR-0005: Identity and authentication

- Status: Accepted, 2026-11-09 (sponsor chose email + password + mandatory TOTP). Outbound mail: Brevo (chosen 2026-11-09). Implemented 2026-11-13 with the parameters in "Implementation decisions" below (sponsor sign-off the same day).

## Context
3–10 internal users. The master must know who each caller is to enforce engagement membership, attribute every change, and sign off at the right review level. Laptops are often offline. The firm does not depend on Microsoft Entra ID for this application. The sponsor wants email-based sign-in with MFA, each user enrolling with their own QR code.

## Decision
Accounts are held by the master in its PostgreSQL database. There is no self-registration.

| Element | Proposal |
|---|---|
| Identifier | Work email address |
| First factor | Password (minimum 12 characters, checked against a breached-password list), hashed with the Identity v3 PBKDF2 hasher |
| Second factor | TOTP (RFC 6238), **mandatory for every user**. On first sign-in the user scans a per-user QR code (`otpauth://` URI) with Microsoft Authenticator, Google Authenticator or similar. The TOTP secret is stored encrypted (AES-256-GCM) with a key held only on the VM |
| Recovery | 10 single-use recovery codes issued at enrolment. An admin can reset MFA; every reset is written to the audit log |
| Onboarding | Admin creates the account and sends a one-time invitation link (24 h expiry) by email; the user sets a password and enrols TOTP |
| Session | Master issues a short-lived access token (15 min) and a rotating refresh token (14 days sliding, 90 days absolute, bound to the client installation id). The client stores the refresh token encrypted with Windows DPAPI (current user) |
| Offline | Offline work needs no network sign-in: the client unlocks with the cached session. Sync resumes when the refresh token is still valid; otherwise the user signs in again with password + TOTP |
| Revocation | Disabling a user or removing a laptop revokes its refresh tokens; the next sync is refused |
| Lockout | 5 failed attempts lock the account for 15 minutes; sign-in events go to the audit log |
| Sign-off | Signing off a working paper requires a fresh TOTP code (step-up), so a sign-off cannot be made from an unattended laptop. Sign-off therefore needs a connection to the master |

## Implementation decisions (2026-11-13)

| Topic | Decision | Reason and consequence |
|---|---|---|
| Sign-off step-up | Online only. `POST /api/auth/step-up` verifies a TOTP code that has not been used before and returns a grant valid 5 minutes, single-use, bound to user, working paper id and content hash. Recovery codes are not accepted | Only the master holds the TOTP secret. A reviewer can review offline but signs off when connected. The code that signed in cannot also sign off |
| Enrolment surface | Server-rendered pages on the master: `/account/enrol`, `/account/forgot`, `/account/reset`. No scripts, no cookies; each form is authorised by the e-mailed one-time token | The link in the e-mail opens a browser, so onboarding does not wait for the desktop client |
| System roles | `Admin` and `User`, separate from engagement roles. An administrator manages accounts and has no implicit engagement membership or sign-off right. The last active administrator cannot be disabled or demoted | Segregation of duties between account administration and audit work. Keep at least two administrators |
| First administrator | `sudo audit-admin invite <email> "<name>" --admin` on the VM. The same command covers list, disable/enable, role, reset-mfa, devices and revoke | Console access is the root of trust and the recovery path. Each console action is in the audit log with the sudo user's name |
| Administrator API | `/api/admin/users` (list, invite, disable/enable, role, reset MFA, devices, revoke). No call returns an invitation link | The link reaches only the user's mailbox, so an administrator cannot activate an account in another person's name |
| Password reset | Self-service by e-mailed link, valid 30 minutes, at most 3 requests per hour per account. The reset signs every device out. Sign-in still needs TOTP | Control of the mailbox alone does not open the account |
| Lost authenticator | Recovery code at sign-in, or administrator `reset-mfa`: TOTP and recovery codes are cleared, sessions end, and the user re-enrols through an e-mailed link that also asks for the current password | Neither the mailbox alone nor an administrator alone can take over the second factor |
| Refresh tokens | 14 days sliding, 90 days absolute. Rotated on every use. A rotated token presented again revokes the session, except once within 30 seconds when its replacement was never used (reply lost on a poor connection) | Laptops on unreliable links are not signed out by a dropped reply; a copied token is still detected |
| Revocation | Every authenticated request checks that the session is live and the user active. The sync connection closes when its access token expires | Disabling a user or removing a laptop takes effect on the next request, not after 15 minutes |
| Lockout | 5 failures lock the account for 15 minutes. Wrong passwords, wrong TOTP codes, wrong recovery codes and wrong step-up codes all count. Attempts for one account are processed one at a time | Parallel guessing cannot outrun the counter. Every failed sign-in gets the same answer, whatever the cause |
| TOTP replay | A code is accepted once: only a time step later than the last accepted one is valid | A code read over the shoulder is useless once used |
| Breached passwords | Bundled list of 46,816 common passwords (12+ characters), plus the haveibeenpwned range API (k-anonymity: 5 hex characters of the SHA-1 leave the VM). If the API cannot be reached the bundled list decides | One outbound dependency, never blocking |
| Key material | One master key (`Auth__MasterKey`, 32 random bytes) in `/etc/audit-master/master.env`. HKDF-SHA256 derives separate keys for access-token signing (HS256), secret encryption and token hashing. Managed with `sudo audit-auth-key` | Replaces the earlier plan to use ASP.NET Core Data Protection: same root of trust (the VM's `master.env`), fewer moving parts, nothing extra in the database to back up. Losing the key forces every user to re-enrol TOTP and sign in again; no engagement data is lost |
| Stored secrets | Passwords: Identity v3 PBKDF2 hasher. Refresh tokens, one-time links, recovery codes and step-up grants: keyed hash (HMAC-SHA256) only. TOTP secrets: AES-256-GCM bound to the user id | A copy of the database without the master key yields no usable credential |
| Account storage | The master's own `users`, `sessions`, `refresh_tokens`, `user_tokens`, `recovery_codes`, `step_up_grants` tables. The ASP.NET Core Identity framework is not used beyond its password hasher | The flows above (mandatory TOTP, bound step-up grants, session revocation) are simpler to state and test directly than to bend Identity's defaults |

Audit log actions: `auth.invite.sent`, `auth.totp.enrolled`, `auth.login.succeeded`, `auth.login.refused`, `auth.attempt.failed`, `auth.account.locked`, `auth.logout`, `auth.session.revoked`, `auth.sessions.revoked`, `auth.password.reset_requested`, `auth.password.reset`, `auth.mfa.reset`, `auth.user.disabled`, `auth.user.enabled`, `auth.user.role_changed`, `auth.step_up.issued`, `auth.step_up.consumed`, `auth.mail.failed`.

## Alternatives considered
- **Email one-time code or magic link only** (no password, no authenticator). Simpler for users, but anyone who controls the mailbox controls the account, and every sign-in depends on mail delivery. Not recommended as the only factor for engagement files.
- **Microsoft Entra ID.** Not used: no dependency on the firm's Microsoft tenant for this application.

## Consequences
- The master sends invitation and reset emails through the Brevo transactional email HTTP API (`POST https://api.brevo.com/v3/smtp/email`, API key in the `api-key` header) only; daily sign-in does not depend on email. The sender domain (`audit.io.vn`) must be authenticated in Brevo with SPF, DKIM and DMARC records in Cloudflare DNS, so that invitations are not filtered as spam. The Brevo API key is entered on the VM only. (Changed from SMTP on 2026-11-13: SMTP keys require phone verification on the Brevo account.)
- `Auth__MasterKey` must be stored offline next to the backup key (`sudo audit-auth-key --show`); losing it forces every user to re-enrol TOTP.
- Without `Auth__MasterKey` the master stays fail-closed: authentication endpoints answer 503 and no token can be issued or validated.
- Open items: the client address seen by the master is Cloudflare's edge until Caddy is told to trust Cloudflare's ranges, so the per-address rate limit (30 requests a minute) and the address in the audit log are coarse; account lockout does not depend on it. One-time tokens appear in the Caddy access log (`/var/log/caddy`, root only) as part of the URL; they are single-use and short-lived. Expired sessions and tokens are not purged yet.
