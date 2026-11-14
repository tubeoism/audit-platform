# Archiving a locked engagement

A locked engagement is read-only, but it still lives in PostgreSQL and on the VM's evidence volume, both of which can be
lost or restored to an older state. Archiving takes the finished file out of that world: one self-contained package,
checked, stored in the R2 bucket `audit-archive` (object lock, immutable for 10 years), and only then is the engagement
marked `Archived`. A correction after the lock is made as an addendum in a new engagement; the archive is never edited.

This document describes the master side (the package and the status change). The upload to `audit-archive` is a separate
step on the VM and is **not built yet**; until it is, `engagement archived` must not be run.

## Three steps, in this order

| # | Command (on the VM, as administrator) | What it does | Changes the engagement? |
|---|---|---|---|
| 1 | `sudo audit-admin engagement package <code> /srv/audit/archive-staging/<code>.tar` | Builds the package, reads it back and checks every file against `SHA256SUMS`, prints `tar-sha256` and `package-digest` | No |
| 2 | upload the tar to `audit-archive` and check that the object there hashes to `tar-sha256` | Not built yet | No |
| 3 | `sudo audit-admin engagement archived <code> <package-digest> <tar-sha256> "<where it is stored>"` | `Locked -> Archived` | Yes, once, and it cannot be taken back |

Order matters: `Archived` has to mean "a verified copy exists in the immutable archive". Step 3 accepts only a package
the master itself built and recorded in step 1 (`engagement.archive.packaged`, with both digests); invented or mistyped
digests are refused and logged.

The output must be under `/srv/audit`: `audit-admin` runs in a sandbox that can write nowhere else.

## What is checked before a byte is written

All problems are reported together; nothing is written if there is any.

- The engagement is `Locked` (or `Archived`, for a second copy).
- The audit log has the `engagement.locked` entry, and the digest recomputed from the database now (change-log head and
  every paper's content hash) equals the digest in that entry, and the number of papers equals the one recorded. In short:
  what is in the database is what the manager locked.
- Every working paper's stored content hash equals the hash recomputed from its state.
- Every audit-log entry about the engagement hashes to its own stored hash.
- Every evidence file with a receipt has an object record and is present in the store.

Evidence is read through the store's verified open (re-hashed on every read), hashed again with SHA-256 for the list, and then copied. After the file is written it is read back
once more: every file in the tar is hashed and compared with `SHA256SUMS`, which must list exactly the files present; the
same pass produces the SHA-256 of the whole tar. A package that exists on disk is a package that verifies.

## The package

One POSIX tar (`engagement-<engagement id>/` as the only top-level folder). The same data and the same lock time give
the same bytes except `manifest.json`, which records when it was generated.

| File | Content |
|---|---|
| `README.txt` | What the package is and how to check it, in Vietnamese and English |
| `manifest.json` | Format version, engagement, lock time and digest, change-log head, counts, audit-chain head at packaging time, generator version |
| `engagement.json` | Engagement, who locked it, members with roles (names and e-mail addresses) |
| `papers/<id>.json` | Each working paper in full: template, status, content hash, preparer, and its whole state (cells, notes, conflicts, sign-offs) |
| `changes.jsonl` | The change log in order of receipt; `payloadBase64` is the operation as sent |
| `audit-log.jsonl` | Every audit-log entry about the engagement, with `previousHash` and `hash` |
| `evidence/index.json` | Every evidence file and every receipt (who, when, original file name) |
| `evidence/files/<blake3>` | The evidence files, named by the BLAKE3-256 of their content: the address every record in the system uses (`b3sum` can check it). `SHA256SUMS` lists their SHA-256 |
| `SHA256SUMS` | SHA-256 of every other file; its own SHA-256 is the **package digest** |

Anyone can check a package with `tar -xf package.tar && sha256sum -c engagement-*/SHA256SUMS`, with no software of ours.
The audit-log lines keep their hash chain fields, but the entries of other engagements between them are not included, so
the chain cannot be re-walked from the package alone; the manifest records the global chain head at packaging time.

## Audit log

`engagement.archive.packaged` (digests, size, counts), `engagement.archive.refused` (blocked, failed, or a package the
master does not know), `engagement.archived` (digests, where it is stored, which packaging entry it refers to).

## Not done yet

- The upload to `audit-archive` (encryption, the R2 token's reach, checking the object after upload) and the script that
  runs steps 1 to 3 together.
- A restore/read-back drill for the archive, like the ones for the database and the evidence mirror.
- The client does not show `Archived` differently from `Locked` (both read-only).
