# ADR-0004: Sync model — field-level versioning vs CRDT

- Status: Open. Decided in spike week 1 against a real procedure form.

## Context
Working papers are mostly structured fields, and the master is always online. A full CRDT may be unnecessary; field-level versioning with master arbitration and flagged conflicts could be enough and is implementable in plain .NET.

## What the skeleton fixes now
- Wire format is engine-neutral: `ChangeEnvelope` carries an opaque payload tagged by `PayloadKind`.
- `IDocumentSyncEngine` is the only seam the master uses. The master's `SyncCoordinator` enforces membership, engagement lock, payload kind and author identity before any engine runs, and records each push in the audit log.
- Until this ADR is accepted, the registered engine is `SyncEngineNotSelected`, which supports no payload kind, so the master accepts no data.

## Criteria for the decision
1. Converges after 3 days of offline edits by 3 clients (spike week 3).
2. Never silently overwrites a reviewer's note or a conclusion; meaning-changing conflicts are surfaced for a person to resolve.
3. Supports sign-off sealing: the signed content hash must be reproducible from the merged state.
4. Operable by a small team without native-library build problems on Windows and linux-arm64.
