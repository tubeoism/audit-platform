# Large evidence files: upload in parts

A single request to the master cannot exceed 95 MB (the limit of the Cloudflare proxy in front of it). Evidence files
above the client's threshold (`ClientOptions.PartedUploadThreshold`, default 95 MB) therefore travel in parts. The
client accepts files up to `ClientOptions.MaxEvidenceBytes` (default 1 GiB); the master takes them up to
`Master:MaxPartedEvidenceBytes` (default 1 GiB).

## Protocol

| Step | Request | Answer |
|---|---|---|
| State | `GET /api/engagements/{e}/evidence/{hash}/parts?total=N` | `{partBytes, maxBytes, received:[…]}` |
| Part | `PUT …/parts/{n}?total=N` (n from 1, `Content-Length` = exactly the expected size) | 204; 400 if the size is wrong |
| Complete | `POST …/complete?total=N&name=…` | 200 with the stored file; 409 `incomplete` if a part is missing; 422 if the whole does not hash to `{hash}` |

Part size is stated by the master (`Master:EvidencePartBytes`, default 32 MB); the last part is shorter.

## Guarantees and controls

| Concern | Behaviour |
|---|---|
| Authorisation | Same as a whole-file upload: member of the engagement who may edit content, engagement not locked. Checked on every call. |
| Integrity | Parts wait under `<EvidenceRoot>/.staging/parts/<engagement>/<hash>`. Completion streams them in order into the ordinary intake, which verifies BLAKE3 against the address before the file enters the store, the database or the audit log. |
| Atomic parts | A part is written to a temp file and moved into place, so a broken connection loses at most the part in flight. |
| Resume | The client asks which parts the master holds and sends only the rest. A part that vanished (clean-up) is reported as "unreachable", not "refused", so the outbox retries. |
| Clean-up | Staging is deleted after success or hash mismatch; uploads untouched for 7 days are swept (at most hourly). The R2 mirror excludes `.staging/**`. |
| Audit | One `evidence.received` entry, written at completion only. |

## Not covered

- Not exercised through Cloudflare or against the live master (the test uses 4 KB parts in-process).
- No parallel part upload; no progress indicator in the WPF screen beyond the existing pending-file count.
