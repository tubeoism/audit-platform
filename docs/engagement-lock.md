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

Refusals on the audit log: `engagement.lock.refused` (preconditions), `engagement.lock.step_up_refused` (grant).

## Known limits

- Changes a member made offline and had not yet pushed when the lock happened are refused by the master. They stay on that
  laptop as "bị từ chối" and are never sent again. The manager should ask the team to sync before locking.
- No e-mail is sent on lock yet (the hub event and the audit log are the record).
- Packaging the locked engagement into `audit-archive` (status `Archived`) is a separate step.
