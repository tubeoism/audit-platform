# Engagement phases and the lock

Decided by Tu on 2026-11-14: the engagement manager locks from the client with a fresh authenticator code, under strict
preconditions, and a lock is never reopened. A correction after the lock is made as an addendum in a new engagement.

## Phases

`Planning -> Fieldwork -> Review -> Reporting`, with `Review -> Fieldwork` and `Reporting -> Review` allowed (the rules are
`Engagement.CanMove` in the domain). Only an **engagement manager of that engagement** moves them (`POST /api/engagements/{id}/status`).
The administrator has no way round this; the console only lists status. Each move, and each refused move, is on the audit log
(`engagement.status.changed`, `engagement.status.refused`). Locking is never a plain move.

## Lock preconditions (checked by the master under the engagement row lock)

| Condition | Why |
|---|---|
| Status is `Reporting` | The file has been through review and is being finalised |
| At least one working paper | An empty file is not a deliverable |
| Every paper is `JapanDeskSignedOff` (both review levels, seal intact) | Q8: two review levels |
| No open review note on any paper | Notes raised after sign-off do not break the seal, so they are checked here |
| No unresolved conflict on any paper | ADR-0004 |

`GET /api/engagements/{id}/lock-readiness` (any member) lists what stands in the way and returns a **digest**: a SHA-256
over the engagement id, the head of its change log and every paper's content hash.

## The lock

`POST /api/engagements/{id}/lock` needs the manager role, a live session and a single-use step-up grant bought with a fresh
TOTP code for that digest (the same mechanism as a sign-off, with the engagement id in place of the paper id). If anything changed after the manager looked, the digest differs, the grant
does not fit and nothing is locked. The grant is not spent when preconditions fail. On success the master sets
`Locked`, `LockedAt`, `LockedBy`, writes `engagement.locked` (digest, head, paper count) and tells every connected member
(`EngagementLocked`); laptops that were offline learn it from the directory at their next start.

Every other member of the engagement (including reviewers) also gets an e-mail, bilingual, naming the engagement code, who locked it and
when. The rows (`notifications`, kind `engagement-locked`) are written in the same transaction as the lock, so a crash between lock and
mail loses nothing; delivery is at-least-once with the existing dispatcher, and each sent mail is audited as
`notify.engagement-locked.sent`. The manager who locked is not mailed.

Refusals on the audit log: `engagement.lock.refused` (preconditions), `engagement.lock.step_up_refused` (grant).

## Known limits

- Changes a member made offline and had not yet pushed when the lock happened are refused by the master. They stay on that
  laptop as "bị từ chối" and are never sent again. The manager should ask the team to sync before locking.
- The e-mail below carries no engagement content (only code, who locked, when); a member whose address bounces is retried for
  12 attempts like any notification and the lock itself is not affected.
- Packaging the locked engagement into `audit-archive` (status `Archived`) is a separate step: see [engagement-archive.md](engagement-archive.md). The package and the status change exist; the upload to the immutable bucket does not yet.
