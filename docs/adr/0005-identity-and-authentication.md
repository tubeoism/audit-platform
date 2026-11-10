# ADR-0005: Identity and authentication

- Status: Accepted, 2026-11-09 (sponsor chose email + password + mandatory TOTP). Outbound mail: Brevo (chosen 2026-11-09).

## Context
3–10 internal users. The master must know who each caller is to enforce engagement membership, attribute every change, and sign off at the right review level. Laptops are often offline. The firm does not depend on Microsoft Entra ID for this application. The sponsor wants email-based sign-in with MFA, each user enrolling with their own QR code.

## Decision
Accounts are held by the master (ASP.NET Core Identity on the master PostgreSQL database). There is no self-registration.

| Element | Proposal |
|---|---|
| Identifier | Work email address |
| First factor | Password (minimum 12 characters, checked against a breached-password list), hashed with the Identity v3 PBKDF2 hasher |
| Second factor | TOTP (RFC 6238), **mandatory for every user**. On first sign-in the user scans a per-user QR code (`otpauth://` URI) with Microsoft Authenticator, Google Authenticator or similar. The TOTP secret is stored encrypted with ASP.NET Core Data Protection keys held on the VM |
| Recovery | 10 single-use recovery codes issued at enrolment. An admin can reset MFA; every reset is written to the audit log |
| Onboarding | Admin creates the account and sends a one-time invitation link (24 h expiry) by email; the user sets a password and enrols TOTP |
| Session | Master issues a short-lived access token (15 min) and a rotating refresh token (14 days, bound to the client installation id). The client stores the refresh token encrypted with Windows DPAPI (current user) |
| Offline | Offline work needs no network sign-in: the client unlocks with the cached session. Sync resumes when the refresh token is still valid; otherwise the user signs in again with password + TOTP |
| Revocation | Disabling a user or removing a laptop revokes its refresh tokens; the next sync is refused |
| Lockout | 5 failed attempts lock the account for 15 minutes; sign-in events go to the audit log |
| Sign-off | Signing off a working paper requires a fresh TOTP code (step-up), so a sign-off cannot be made from an unattended laptop |

## Alternatives considered
- **Email one-time code or magic link only** (no password, no authenticator). Simpler for users, but anyone who controls the mailbox controls the account, and every sign-in depends on mail delivery. Not recommended as the only factor for engagement files.
- **Microsoft Entra ID.** Not used: no dependency on the firm's Microsoft tenant for this application.

## Consequences
- The master sends invitation and reset emails through Brevo transactional SMTP (`smtp-relay.brevo.com:587`, STARTTLS) only; daily sign-in does not depend on email. The sender domain (`audit.io.vn`) must be authenticated in Brevo with SPF, DKIM and DMARC records in Cloudflare DNS, so that invitations are not filtered as spam. The Brevo SMTP key is entered on the VM only.
- Data Protection keys must be backed up with the database; losing them forces every user to re-enrol TOTP.
- Until this ADR is implemented the master keeps the fail-closed `Pending` scheme and answers 401 to everyone.
