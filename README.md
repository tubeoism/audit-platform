# Audit Platform

Working-paper platform for internal audit and fraud investigation engagements under AUP contracts. Auditors work offline on Windows laptops; a master node on an Oracle Cloud Arm VM is the system of record, coordinates real-time sync, enforces engagement lock and sign-off, and holds the archive.

Status: **skeleton with authentication**. Domain rules, contracts, audit log, evidence store, database schema and authentication (ADR-0005) are in place and tested. The sync engine (ADR-0004) is deliberately not chosen yet; until it is, the master accepts signed-in users but no document changes.

## Solution layout

| Project | Target | Role |
|---|---|---|
| `src/AuditPlatform.Domain` | net10.0 | Engagement lifecycle and lock, procedure templates (parser for `ia-aup-procedures` Markdown, EN/VI), working papers, review notes, two-level sign-off with content-hash seal |
| `src/AuditPlatform.Contracts` | net10.0 | SignalR hub interfaces (`ISyncHub`, `ISyncClient`), engine-neutral `ChangeEnvelope`, authentication API contracts |
| `src/AuditPlatform.Sync` | net10.0 | `IDocumentSyncEngine` seam; `SyncEngineNotSelected` placeholder |
| `src/AuditPlatform.AuditLog` | net10.0 | Hash-chained audit log (SHA-256), independent verifier |
| `src/AuditPlatform.Evidence` | net10.0 | Content-addressed evidence store (BLAKE3-256), verify-on-read |
| `src/AuditPlatform.Master.Data` | net10.0 | PostgreSQL schema (EF Core + Npgsql), migrations, append-only trigger, `PostgresAuditLog` |
| `src/AuditPlatform.Master` | net10.0, linux-arm64 | ASP.NET Core master: authentication (accounts, TOTP, sessions, sign-off step-up, admin API and console command), sync hub, `SyncCoordinator` policy layer, health checks |
| `src/AuditPlatform.Client.Core` | net10.0 | Client logic: SQLite replica, outbox, `SyncAgent`, SignalR transport, `AuthSession` (sign-in, token renewal, sign-off grant), view models |
| `src/AuditPlatform.Client.Wpf` | net10.0-windows | WPF shell (thin) |
| `tests/*` | net10.0 | xUnit v3 on Microsoft.Testing.Platform |

Dependency direction: `Domain` ← `Master.Data` ← `Master`; `Contracts` ← `Sync` ← `Master`; `Contracts`/`Domain` ← `Client.Core` ← `Client.Wpf`. The client never references master projects.

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
| Sign off | Client, `POST /api/auth/step-up` | A fresh TOTP code buys a 5-minute single-use grant bound to one working paper and content hash (`ISignOffStepUp.ConsumeAsync`) |
| Forgot password | Browser, `/account/forgot` | Reset link valid 30 minutes; every device is signed out; TOTP still required |

To run the master locally with authentication, set a throw-away key and take the invitation link from the console (no mail is sent without a Brevo key):

```bash
export Auth__MasterKey=$(head -c 32 /dev/urandom | base64) Auth__PublicBaseUrl=http://localhost:5080/
dotnet run --project src/AuditPlatform.Master -- admin invite you@example.com "Your Name" --admin --show-link
dotnet run --project src/AuditPlatform.Master
```

## Security rules already enforced

- Nobody reaches the sync hub or the API without a live session; a disabled user or a revoked laptop is refused on the next request.
- Five failed attempts lock an account for 15 minutes; every failed sign-in gets the same answer; each TOTP code works once.
- The database holds no usable credential: passwords are PBKDF2 hashes, tokens and recovery codes are keyed hashes, TOTP secrets are encrypted with a key that exists only on the VM.
- Account events (invite, enrolment, sign-in, failure, lockout, reset, revocation, sign-off grant) are written to the hash-chained audit log.

- The master ignores the author id a client claims and uses the authenticated identity.
- Non-members get the same error whether or not an engagement exists.
- Locked or archived engagements reject every change, on the master and in the client outbox.
- `audit_log` rejects UPDATE, DELETE and TRUNCATE at database level; tampering that bypasses this is caught by chain verification.
- Evidence is re-hashed on every read; a modified file raises `EvidenceIntegrityException`.
- No secrets in the repository. Production configuration comes from `/etc/audit-master/master.env` on the VM (see `deploy/`).

## Decisions

See [docs/adr](docs/adr/README.md). Design background: feasibility study, sections 7 and 9.1.
