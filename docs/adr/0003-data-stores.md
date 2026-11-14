# ADR-0003: PostgreSQL on master, SQLite on clients, evidence outside the database

- Status: Proposed (recommended 2026-11-09, awaiting sponsor sign-off)

## Context
The master is the system of record and must support point-in-time restore. Oracle Autonomous Database Always Free was rejected: no backup/restore, no private endpoint, auto-stop after 7 days of inactivity. Engagements are 2–3 GB each, mostly evidence files.

## Decision
- Master: PostgreSQL 16 on the same VM, accessed through EF Core + Npgsql, snake_case schema. Backup by WAL-G continuous archiving plus nightly base backup to Cloudflare R2 bucket `audit-live` (RPO minutes).
- Client: SQLite per laptop for the offline replica and the sync outbox, on a BitLocker-encrypted volume.
- Evidence: content-addressed files (BLAKE3-256) on the VM block volume, mirrored (encrypted) to R2 audit-live/evidence (see deploy/master/evidence); the database holds only hashes and the intake log.
- Audit log: `audit_log` table, hash-chained (SHA-256), append-only enforced by trigger. The hashed payload is stored as `text`, not `jsonb`, because `jsonb` re-serialises JSON and would break the hash; timestamps are truncated to microseconds for the same reason (verified against PostgreSQL 16).

## Consequences
- Standard tooling for backup, restore and inspection; the database stays small.
- The VM is a single point of failure until restore is proven (spike week 5) and the manager-laptop failover runbook exists.
