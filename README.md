# Audit Platform

Working-paper platform for internal audit and fraud investigation engagements under AUP contracts. Auditors work offline on Windows laptops; a master node on an Oracle Cloud Arm VM is the system of record, coordinates real-time sync, enforces engagement lock and sign-off, and holds the archive.

Status: **master with authentication and durable sync; first client slice** (sign-in, engagements, templated working papers, sync loop, conflict decision). Evidence, review notes and sign-off screens, and the engagement lock screen follow. Domain rules, contracts, audit log, evidence store, database schema, authentication (ADR-0005) and the sync engine (ADR-0004: field-level versioning with master arbitration, PostgreSQL log on the master, SQLite replica on the client, SignalR transport, TOTP step-up at sign-off) are in place and tested.

## Solution layout

| Project | Target | Role |
|---|---|---|
| `src/AuditPlatform.Domain` | net10.0 | Engagement lifecycle and lock, procedure templates (parser for `ia-aup-procedures` Markdown, EN/VI), working papers, review notes, two-level sign-off with content-hash seal |
| `src/AuditPlatform.Contracts` | net10.0 | SignalR hub interfaces (`ISyncHub`, `ISyncClient`), `ChangeEnvelope`, sign-off and authentication API contracts |
| `src/AuditPlatform.Sync` | net10.0 | ADR-0004 model shared by master and client: field-v1 operations and the deterministic working-paper reducer (`FieldDocument`) |
| `src/AuditPlatform.AuditLog` | net10.0 | Hash-chained audit log (SHA-256), independent verifier |
| `src/AuditPlatform.Evidence` | net10.0 | Content-addressed evidence store (BLAKE3-256), verify-on-read |
| `src/AuditPlatform.Master.Data` | net10.0 | PostgreSQL schema (EF Core + Npgsql), migrations, append-only triggers on `audit_log` and `changes`, `PostgresAuditLog` |
| `src/AuditPlatform.Master` | net10.0, linux-arm64 | ASP.NET Core master: authentication (accounts, TOTP, sessions, sign-off step-up, admin API and console command), sync hub, `SyncCoordinator` policy layer, `FieldSyncEngine` (durable engine), sign-off API, reviewer notifications, health checks |
| `src/AuditPlatform.Client.Core` | net10.0 | Client logic: SQLite replica (`LocalReplica`) with migrations, outbox, `SyncAgent`, `SyncLoop`, `EngagementWorkspace` (edits as field-v1 changes), directory cache (engagements, members, templates), working-paper layout, SignalR transport, `AuthSession`, view models |
| `src/AuditPlatform.Client.Wpf` | net10.0-windows | WPF shell (thin): sign-in window, main window with engagements, working papers and the form |
| `tests/*` | net10.0 | xUnit v3 on Microsoft.Testing.Platform |

Dependency direction: `Domain` ← `Master.Data` ← `Master`; `Contracts`/`Domain` ← `Sync` ← `Master`; `Contracts`/`Domain`/`Sync` ← `Client.Core` ← `Client.Wpf`. The client never references master projects.

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build AuditPlatform.slnx
dotnet test --solution AuditPlatform.slnx
```

Database tests run only when `AUDIT_TEST_PG` names a PostgreSQL connection string with permission to create databases; otherwise they are skipped:

```bash
AUDIT_TEST_PG="Host=localhost;Database=postgres;Username=audit_dev;Password=audit_dev" dotnet test --solution AuditPlatform.slnx
```

Create or update the master schema:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/AuditPlatform.Master.Data -o Migrations
dotnet ef database update --project src/AuditPlatform.Master.Data --connection "<connection string>"
```

Run the master locally (needs a local PostgreSQL matching `appsettings.Development.json`):

```bash
dotnet run --project src/AuditPlatform.Master
curl http://localhost:5080/healthz/live
```

The WPF client builds on any OS but runs only on Windows: `dotnet run --project src/AuditPlatform.Client.Wpf`.

## Authentication (ADR-0005)

| Step | Where | What happens |
|---|---|---|
| Invite | `sudo audit-admin invite <email> "<name>" [--admin]` on the VM, or `POST /api/admin/users` as an administrator | One-time link by e-mail, valid 24 hours |
| Enrol | Browser, `/account/enrol?token=...` | Set a password (12+ characters, breached passwords refused), scan the QR code, confirm a code, save 10 recovery codes |
| Sign in | Client, `POST /api/auth/login` | Password + TOTP (or one recovery code). Returns a 15-minute access token and a rotating refresh token bound to the installation |
| Stay signed in | Client, `POST /api/auth/refresh` | 14 days sliding, 90 days absolute. Reuse of a rotated token revokes the session |
| Sign off | Client, `POST /api/auth/step-up` then `POST /api/sync/sign-off` | A fresh TOTP code buys a 5-minute single-use grant bound to one working paper and content hash. The sign-off spends it in its own transaction and seals the hash the master computed; if the master's content differs from what the reviewer saw, the grant does not fit |
| Forgot password | Browser, `/account/forgot` | Reset link valid 30 minutes; every device is signed out; TOTP still required |

To run the master locally with authentication, set a throw-away key and take the invitation link from the console (no mail is sent without a Brevo key):

```bash
export Auth__MasterKey=$(head -c 32 /dev/urandom | base64) Auth__PublicBaseUrl=http://localhost:5080/
dotnet run --project src/AuditPlatform.Master -- admin invite you@example.com "Your Name" --admin --show-link
dotnet run --project src/AuditPlatform.Master
```

## Engagements and the methodology library

Set up from the VM console; every command is written to the audit log. An administrator gets no access to an engagement by setting it up.

```bash
sudo audit-admin engagement create ENG-2026-014 "Công ty ABC" "AUP-2026-014" 2026-01-01 2026-12-31
sudo audit-admin engagement member ENG-2026-014 an@audit.io.vn preparer        # preparer | managerqa | japandesk | engagementmanager
sudo audit-admin engagement list
# One call per methodology file of ia-aup-procedures; a procedure whose text changed becomes the next version.
for f in IA_AUP_Standard_Vi/[0-9]*.md; do sudo audit-admin templates import vi < "$f"; done
for f in IA_AUP_Standard_En/[0-9]*.md; do sudo audit-admin templates import en < "$f"; done
```

Who may do what inside an engagement is the table "Authorisation" in [ADR-0004](docs/adr/0004-sync-model.md).

## Client

`dotnet run --project src/AuditPlatform.Client.Wpf` (Windows). Settings in `appsettings.json`: `Client:MasterUrl`, `Client:TemplateLanguage` (`vi` or `en`). Data is under `%LOCALAPPDATA%\AuditPlatform` (SQLite database, DPAPI-protected session); the volume must be BitLocker-encrypted.

- Sign in once with password and authenticator code; afterwards the client opens offline from the stored session.
- A box on the form is saved when the cursor leaves it; the status line shows what is waiting to be sent.
- Two people changing the same box: both values are kept and shown side by side until the paper's preparer or the engagement manager decides.
- To change the local schema: `dotnet ef migrations add <Name> --project src/AuditPlatform.Client.Core -o LocalStore/Migrations`.

## Security rules already enforced

- Nobody reaches the sync hub or the API without a live session; a disabled user or a revoked laptop is refused on the next request.
- Five failed attempts lock an account for 15 minutes; every failed sign-in gets the same answer; each TOTP code works once.
- The database holds no usable credential: passwords are PBKDF2 hashes, tokens and recovery codes are keyed hashes, TOTP secrets are encrypted with a key that exists only on the VM.
- Account events (invite, enrolment, sign-in, failure, lockout, reset, revocation, sign-off grant) are written to the hash-chained audit log.

- The master ignores the author id a client claims and uses the authenticated identity.
- Non-members get the same error whether or not an engagement exists; the engagement list shows a user only their own.
- Inside an engagement the role decides: reviewers do not write content, preparers do not raise review notes, a conflict is decided by the paper's preparer or the engagement manager. The master enforces it; the client only mirrors it.
- Unsent changes of one person cannot be delivered by another person signing in on the same laptop.
- Locked or archived engagements reject every change, on the master and in the client outbox.
- `audit_log` rejects UPDATE, DELETE and TRUNCATE at database level; tampering that bypasses this is caught by chain verification.
- Evidence is re-hashed on every read; a modified file raises `EvidenceIntegrityException`.
- No secrets in the repository. Production configuration comes from `/etc/audit-master/master.env` on the VM (see `deploy/`).

## Decisions

See [docs/adr](docs/adr/README.md). Design background: feasibility study, sections 7 and 9.1.
