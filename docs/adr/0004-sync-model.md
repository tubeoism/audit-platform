# ADR-0004: Sync model — field-level versioning with master arbitration

- Status: Proposed (recommended 2026-11-13 after the spike, awaiting sponsor sign-off)
- Spike: `tests/AuditPlatform.Sync.Spike` (29 tests, run by CI with the rest of the solution)

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
3. Simultaneous typing in one text field: open; see "Long text" below.

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
and it is exactly what produces S3. Recommended: whole-field replacement now. If it is wanted later, a text field can
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
- The master stays fail-closed (`SyncEngineNotSelected`) until the durable engine exists. The spike keeps state in memory.

## Follow-up for the implementation
1. Move the reducer and operations to `src/AuditPlatform.Sync`; PostgreSQL store for the log and document state on the
   master, SQLite replica on the client; write per-change before/after to `audit_log`.
2. `SyncAgent` currently orders the outbox by the client clock and treats `Duplicate` as delivered. Order by a local
   sequence instead; the engine already returns the original outcome on redelivery.
3. Notify reviewers when a seal breaks; conflict list and resolution screen in the client.
4. Delete the spike project and the YDotNet packages once the engine in `src` has taken over its scenarios.
