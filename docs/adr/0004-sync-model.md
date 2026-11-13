# ADR-0004: Sync model — field-level versioning with master arbitration

- Status: Accepted (2026-11-13, sponsor sign-off after the spike); implemented 2026-11-13, see "Implementation"
- Evidence: scenarios S1 to S11 in `tests/AuditPlatform.Sync.Tests`, run by CI against PostgreSQL and SQLite. The spike
  project that compared the two candidates (and its YDotNet packages) was removed once the engine took over its scenarios;
  it is in the history at commit `af4bd0a`.

## Context
Working papers are mostly structured fields, and the master is always online. Clients are offline most of the time and
synchronise over SignalR when they have a connection. Two models were candidates:

- (a) **Field-level versioning with master arbitration.** A client sends intents ("set field X to V, based on version B").
  The master orders them, decides each one, and serves the ordered log back. Plain .NET.
- (b) **CRDT.** Every replica merges every edit automatically. Tested with Yrs through YDotNet 0.7.0 (native Rust
  library), plus a hand-written last-writer-wins map as a control.

Sponsor decisions taken during the spike (2026-11-13):

1. Two people editing the same field: keep both values and wait for a person to decide. No "last one wins".
2. Editing after sign-off is allowed and breaks the seal automatically.
3. Option (a) is approved, with its compensating controls for a broken seal. No CRDT is needed; long text is replaced
   as a whole field (see "Long text" below).

## Decision
Adopt (a). Payload kind `field-v1`.

### Rules
| Area | Rule |
|---|---|
| Order | The master's arrival order is the only order. Client clocks are informational and never compared. |
| Field version | A field's version is the id of the change that produced its current value. `SetField` carries the version the author was looking at. |
| Accept | Base version is current, or the proposed value equals the current value. |
| Conflict | Base version is stale and the value differs: the master keeps its value, records the proposal as an open conflict visible to every member, and returns `Conflict` to the author. Nothing is overwritten and nothing is dropped. |
| Own offline saves | A client chains its unsent saves of one field, so they do not conflict with each other; each save appears in the field history. If the first of the chain conflicts, the latest proposal supersedes the earlier ones. |
| Resolution | `ResolveConflict` names the conflict, the value to keep and the version the resolver was looking at. It is refused if the field changed again. |
| Long text | A text field is one value, replaced as a whole. Concurrent rewrites are a conflict like any other field; text is never blended. |
| Evidence links | Add and remove apply in master order; both are content changes. |
| Review notes | Responses are appended, never replaced. A response to a cleared note is rejected. Clearing names the last response the reviewer read and is rejected if a newer one exists. |
| Sign-off | Master only, online, after TOTP step-up (ADR-0005). The master computes the content hash; each replica recomputes it when it replays the sign-off and fails loudly on a mismatch. Blocked by open review notes and by open conflicts. |
| Edit after sign-off | Accepted. If the content hash changes, every standing sign-off is marked broken by that change id, stays on record, and the paper returns to `ReadyForReview`. A stale edit is flagged as a conflict and does not break the seal; resolving it in favour of the new value does. |
| Engagement lock | Unchanged: `SyncCoordinator` rejects every change. The author keeps the rejected change with its reason; the confirmed replica stays identical to the archive. |
| Redelivery | Idempotent per change id. A redelivered change gets its original outcome again (`Duplicate` only if it was accepted), so a lost answer cannot turn a conflict into "delivered". |
| Convergence | Master and clients run the same deterministic reducer over the same log. A replica that would decide differently stops instead of drifting. |

## Spike results
Same scenarios against both candidates. "Pass" means the control expectation in the scenario was met.

| # | Scenario | (a) Field versioning | (b) CRDT (Yrs) |
|---|---|---|---|
| S1 | Different fields edited offline | Pass: both kept | Pass: both kept |
| S2 | Same field edited offline | Pass: first holds, second flagged, both values kept, resolver and before/after in the trail | **Fail**: one value discarded, both pushes "Accepted", no signal |
| S3 | Concurrent edits of the conclusion | Pass: one author's text holds, the other is kept verbatim in the conflict | **Fail**: "Two exceptions noted in the sample of 25 items. Control operates effectively." A sentence neither author wrote |
| S4 | Review note raised while the field is edited | Pass: note stays open and blocks sign-off | Not differentiating |
| S5 | Reviewer clears while the preparer corrects the response | Pass in both arrival orders: never cleared over an unread response | **Fail**: note ends up cleared with a response the reviewer never read |
| S6 | Evidence added and removed concurrently | Pass | Pass |
| S7 | Edit after sign-off | Pass: accepted, seal broken, broken sign-off kept with the breaking change; hash reproducible on every replica | Partial: hash reproducible and a break is detectable, but only as "a binary update changed the hash" |
| S7 | Stale offline edit arriving after sign-off | Pass: flagged, seal intact until a person decides | **Fail**: may silently replace signed content, depending on internal replica ids |
| S8 | Change after engagement lock | Pass: rejected, kept by the author, replica equals the archive | **Fail**: rejected edit is already merged locally and cannot be removed; replica differs from the archive until it is discarded |
| S9 | Lost answer, double delivery | Pass | Pass |
| S10 | Laptop clock one day ahead | Pass: no influence | Pass for Yrs (no wall clock). The last-writer-wins control fails: the skewed laptop wins silently |
| S11 | 3 clients, 3 days, flaky line, 200 seeds, 21,600 edits | Pass: all replicas and hashes equal; every edit is in the history or in a recorded conflict; 3,971 collisions surfaced | Converges; the same 3,971 collisions produce no signal |
| Trail | What the master receives | Every save, with author, before and after | Overwritten offline values are garbage-collected before sending; updates carry a random replica number, not the user |

The S11 workload is deliberately hostile (three people editing the same eight fields of one paper). It measures
correctness under collisions, not how often collisions happen in fieldwork.

## Against the criteria
| Criterion | (a) | (b) |
|---|---|---|
| 1. Converges after 3 days offline, 3 clients | Met | Met |
| 2. Never silently overwrites a note or a conclusion | Met | Not met (S2, S3, S5, S7) |
| 3. Signed hash reproducible from merged state | Met | Met |
| 4. No native-library burden on Windows and linux-arm64 | Met: plain .NET | Packages ship win-x64 and linux-arm64 binaries; built and run on linux-x64 only. Binding is 0.x and describes itself as early |
| 5. Each change attributable, with before and after | Met | Not met without rebuilding it outside the CRDT |
| 6. Cost for a small team | One reducer, about 400 lines, no dependency | Second data model, native dependency, and the controls of (a) still have to be built around it |

## Long text
Simultaneous typing is technically feasible with (b) but only helps when two people are online in the same paragraph,
and it is exactly what produces S3. Decided: whole-field replacement. If it is wanted later, a text field can
get a live co-editing session between online clients whose result is saved as one ordinary field revision; the sync
model does not change.

## Consequences
- A person must resolve each conflict. Who may resolve (author, preparer of the paper, reviewer) is an authorisation
  rule for the master, to be fixed with the working-paper UI.
- Because editing after sign-off is allowed, any member with edit rights can break a reviewer's seal. The compensating
  controls are: the broken sign-off stays on record with the change and author that broke it, the paper returns to
  review, reviewers must be notified, and the engagement lock remains a hard stop.
- Cross-field consistency (sample size changed by one person, conclusion by another) is not a sync conflict in either
  model; it is caught by review and by the seal.
- The master accepts only payload kind `field-v1`; anything else is rejected change by change.

## Implementation
| Part | Where | What it does |
|---|---|---|
| Reducer | `src/AuditPlatform.Sync` (`FieldDocument`, `FieldOps`) | The rules above as one deterministic fold. Master and client reference the same assembly. |
| Master log | `changes` (PostgreSQL) | Accepted and flagged changes per engagement, sequences 1..n without gaps. UPDATE, DELETE and TRUNCATE are rejected by trigger and revoked from the application role. |
| Master engine | `FieldSyncEngine` | One transaction per push: locks the engagement row, decides each change, appends to `changes`, stores the folded document in `sync_documents`, keeps a receipt per change id in `sync_receipts` (redelivery), writes each decision to `audit_log` with before and after. |
| Engagement lock | `SyncCoordinator`, and again inside the engine's transaction | A change is decided wholly before the lock or wholly after it; after it, everything is rejected and nothing is recorded. |
| Sign-off | `POST /api/sync/sign-off` | Needs a live session, the reviewer's role in the engagement (ManagerQa or JapanDesk) and a step-up grant (ADR-0005) for the content hash the master is about to seal. The grant is spent in the sign-off's transaction, and not spent if the paper's own rules refuse. |
| Broken seal | `FieldSyncEngine`, `NotificationSender` | Audit entry `sync.seal.broken` with the voided sign-offs; a row in `notifications` per reviewer in the same transaction; e-mail with retry (no working-paper content in the mail). Clients also raise `LocalReplica.SealBroken` when they replay the change. |
| Client replica | `LocalReplica` (SQLite: `replica_log`, `documents`) | Replays the master's log with the same reducer, inside the transaction that advances the pull cursor. Stops with `ReplicaDivergedException` on a gap or on a change it would decide differently. |
| Client edits | `EngagementWorkspace`, `SyncAgent` | Edits become field-v1 changes in the outbox, based on the version the user was looking at; pushed in the order they were made (local sequence, not the clock). Conflicts and rejections stay in the outbox with the reason. |

Decisions taken while implementing (all within the rules above):

1. The master keeps a materialised document next to the log. It is derived data, written in the same transaction; tests
   compare it with a replay of the log after every scenario.
2. A client settles its own pending changes from the log as well as from the push answer, so a lost answer needs no
   second round trip. Because of this the S11 collision count is 3,988 instead of the spike's 3,971: an author who has
   seen that their save was flagged no longer chains the next save onto it.
3. A pull from a cursor beyond the end of the master's log (master restored from an older backup) is refused with an
   explicit error instead of serving a different history under the same sequence numbers.
4. Reviewers to notify when a seal breaks: the signers of the voided sign-offs and every member holding ManagerQa or
   JapanDesk in the engagement, except the person who made the change.

## Open after the implementation
1. Who may resolve a conflict, raise or clear a note, or edit a paper is not restricted beyond engagement membership
   (and "only the reviewer who raised a note clears it"). To be fixed with the working-paper UI.
2. Conflict list, resolution screen and the sync loop (connect, subscribe, push/pull on signal) in the WPF client;
   SQLite migrations with the first client release.
3. Rebuild procedure for a replica that diverged or is ahead of a restored master.
4. No API locks an engagement yet; the engine honours the status as soon as one does.
