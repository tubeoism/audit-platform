# Audit Platform

Working-paper platform for internal audit and fraud investigation engagements under AUP contracts. Auditors work offline on Windows laptops; a master node on an Oracle Cloud Arm VM is the system of record, coordinates real-time sync, enforces engagement lock and sign-off, and holds the archive.

Status: **skeleton**. Domain rules, contracts, audit log, evidence store and database schema are in place and tested. The sync engine (ADR-0004) and authentication (ADR-0005) are deliberately not chosen yet; the master is fail-closed until they are.

## Solution layout

| Project | Target | Role |
|---|---|---|
| `src/AuditPlatform.Domain` | net10.0 | Engagement lifecycle and lock, procedure templates (parser for `ia-aup-procedures` Markdown, EN/VI), working papers, review notes, two-level sign-off with content-hash seal |
| `src/AuditPlatform.Contracts` | net10.0 | SignalR hub interfaces (`ISyncHub`, `ISyncClient`) and engine-neutral `ChangeEnvelope` |
| `src/AuditPlatform.Sync` | net10.0 | `IDocumentSyncEngine` seam; `SyncEngineNotSelected` placeholder |
| `src/AuditPlatform.AuditLog` | net10.0 | Hash-chained audit log (SHA-256), independent verifier |
| `src/AuditPlatform.Evidence` | net10.0 | Content-addressed evidence store (BLAKE3-256), verify-on-read |
| `src/AuditPlatform.Master.Data` | net10.0 | PostgreSQL schema (EF Core + Npgsql), migrations, append-only trigger, `PostgresAuditLog` |
| `src/AuditPlatform.Master` | net10.0, linux-arm64 | ASP.NET Core master: sync hub, `SyncCoordinator` policy layer, health checks |
| `src/AuditPlatform.Client.Core` | net10.0 | Client logic: SQLite replica, outbox, `SyncAgent`, SignalR transport, view models |
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

## Security rules already enforced

- The master ignores the author id a client claims and uses the authenticated identity.
- Non-members get the same error whether or not an engagement exists.
- Locked or archived engagements reject every change, on the master and in the client outbox.
- `audit_log` rejects UPDATE, DELETE and TRUNCATE at database level; tampering that bypasses this is caught by chain verification.
- Evidence is re-hashed on every read; a modified file raises `EvidenceIntegrityException`.
- No secrets in the repository. Production configuration comes from `/etc/audit-master/master.env` on the VM (see `deploy/`).

## Decisions

See [docs/adr](docs/adr/README.md). Design background: feasibility study, sections 7 and 9.1.
