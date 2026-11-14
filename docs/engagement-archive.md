# Archiving a locked engagement

A locked engagement is read-only, but it still lives in PostgreSQL and on the VM's evidence volume, both of which can be
lost or restored to an older state. Archiving takes the finished file out of that world: one self-contained package,
checked, stored in the R2 bucket `audit-archive` (object lock, immutable for 10 years), and only then is the engagement
marked `Archived`. A correction after the lock is made as an addendum in a new engagement; the archive is never edited.

The master builds the package and changes the status (`audit-admin engagement package|archived`); the script
`audit-engagement-archive` on the VM runs the whole sequence, including the upload.

## Steps, in this order

| # | What | Changes the engagement? |
|---|---|---|
| 1 | `audit-admin engagement package <code> <file.tar>` builds the package, reads it back and checks every file against `SHA256SUMS`, prints `tar-sha256` and `package-digest` | No |
| 2 | Upload to `r2:audit-archive/archive/engagements/<code>/<file>.tar.bin`, encrypted with the evidence key (rclone crypt, remote `archive`), never overwriting | No |
| 3 | Read the object back from R2, decrypt it, hash it: must equal `tar-sha256` | No |
| 4 | `audit-admin engagement archived <code> <package-digest> <tar-sha256> "<where it is stored>"`: `Locked -> Archived` | Yes, once, and it cannot be taken back |

`sudo audit-engagement-archive <code>` runs 1 to 4; `--package-only` stops after step 1 and leaves the tar in
`/srv/audit/archive-staging` for inspection (do this the first time). The upload asks you to type the engagement code
(`--yes` skips the question). If the read-back differs, nothing is marked archived and the bad object stays in the bucket.

Order matters: `Archived` has to mean "a verified copy exists in the immutable archive". Step 4 accepts only a package
the master itself built and recorded in step 1 (`engagement.archive.packaged`, with both digests); invented or mistyped
digests are refused and logged.

**Objects in `audit-archive` cannot be deleted or overwritten for 10 years.** File names carry a timestamp so that nothing
is overwritten, and a failed attempt leaves its object behind. The same evidence key as the mirror encrypts the packages:
losing it loses the archive as well as the mirror. Packages are read back with the commands in the "Gói lưu trữ" section
of `deploy/master/backup/RESTORE.md`.

The staging folder and the output of step 1 must be under `/srv/audit`: `audit-admin` runs in a sandbox that can write
nowhere else.

## What is checked before a byte is written

All problems are reported together; nothing is written if there is any.

- The engagement is `Locked` (or `Archived`, for a second copy).
- The audit log has the `engagement.locked` entry, and the digest recomputed from the database now (change-log head and
  every paper's content hash) equals the digest in that entry, and the number of papers equals the one recorded. In short:
  what is in the database is what the manager locked.
- Every working paper's stored content hash equals the hash recomputed from its state.
- Every audit-log entry about the engagement hashes to its own stored hash.
- Every evidence file with a receipt has an object record and is present in the store.

Evidence is read through the store's verified open (re-hashed on every read). After the file is written it is read back
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
| `evidence/files/<sha256>` | The evidence files, named by the SHA-256 of their content |
| `SHA256SUMS` | SHA-256 of every other file; its own SHA-256 is the **package digest** |

Anyone can check a package with `tar -xf package.tar && sha256sum -c engagement-*/SHA256SUMS`, with no software of ours.
The audit-log lines keep their hash chain fields, but the entries of other engagements between them are not included, so
the chain cannot be re-walked from the package alone; the manifest records the global chain head at packaging time.

## Audit log

`engagement.archive.packaged` (digests, size, counts), `engagement.archive.refused` (blocked, failed, or a package the
master does not know), `engagement.archived` (digests, where it is stored, which packaging entry it refers to).

## Not done yet

- The first real run. The script is tested only against stand-ins for `rclone` and `audit-admin`; the R2 token's rights on
  `audit-archive/archive/engagements` (multipart upload under the bucket lock) are untested. Use `--package-only` first,
  then a real upload with a small engagement.
- A read-back drill for the archive, like the ones for the database and the evidence mirror.
- The client does not show `Archived` differently from `Locked` (both read-only).
