#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-engagement-archive. Archives a LOCKED engagement into the immutable bucket
# (docs/engagement-archive.md), in the order that keeps "Archived" honest:
#   1. audit-admin engagement package   build the package and check it (read-only for the engagement)
#   2. upload to audit-archive, encrypted with the evidence key (rclone crypt), never overwriting
#   3. read the object back from R2, decrypt it and compare its SHA-256 with the package's
#   4. audit-admin engagement archived  Locked -> Archived (only now; it cannot be taken back)
#
#   sudo audit-engagement-archive <code> --package-only                          step 1 only: leaves the tar in /srv/audit/archive-staging
#   sudo audit-engagement-archive <code>                                         all four, after you type the engagement code to confirm
#   sudo audit-engagement-archive <code> --resume <tar> <tar-sha256> <digest>    steps 2-4 for a tar that --package-only made and you inspected
#   add --yes to skip the question (scripts)
#
# WARNING: objects in audit-archive cannot be deleted or overwritten for 10 years. A wrong upload stays. That is why the
# first run should be --package-only, why the file name carries a timestamp (nothing is ever overwritten), why the upload
# asks for the engagement code, and why, if step 4 fails after a good upload, the script keeps the tar and prints the exact
# command to finish with: running everything again would upload a second, permanent copy. A failed read-back leaves its
# object in the bucket; do not mark it archived.
set -euo pipefail

STAGING="${STAGING:-/srv/audit/archive-staging}"
CONF="${CONF:-/etc/audit-evidence/rclone.conf}"
STATE=/var/lib/audit-backup
RCLONE=/usr/local/bin/rclone
usage() { sed -n '2,15p' "$0" >&2; exit 2; }

CODE="${1:-}"; shift || true
MODE=all; YES=0; RESUME=0; R_TAR=""; R_TAR_SHA=""; R_DIGEST=""
while (($#)); do
  case "$1" in
    --package-only) MODE=package; shift ;;
    --yes) YES=1; shift ;;
    --resume) (($# >= 4)) || usage; RESUME=1; R_TAR="$2"; R_TAR_SHA="$3"; R_DIGEST="$4"; shift 4 ;;
    *) usage ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "Run with sudo" >&2; exit 1; }
# The code is passed to audit-admin as one argument (any characters the master accepts); a flag is not a code.
[[ -n "$CODE" && "$CODE" != -* && ${#CODE} -le 40 ]] || usage
[[ "$MODE" == all || "$RESUME" == 0 ]] || usage
# File names and remote paths use a plain version of it.
SLUG="${CODE//[^A-Za-z0-9._-]/_}"
[[ -x "$RCLONE" && -s "$CONF" ]] || { echo "Evidence mirror is not set up (rclone/config missing): run evidence/evidence-setup.sh first" >&2; exit 1; }
grep -q '^\[archive\]' "$CONF" || { echo "$CONF has no [archive] remote: run sudo audit-evidence-config (or evidence-setup.sh again)" >&2; exit 1; }

install -d -m 0700 "$STATE"
exec 9>"$STATE/engagement-archive.lock"
flock -n 9 || { echo "Another archive run is in progress"; exit 1; }

as_audit() { runuser -u audit -- "$@"; }
UPLOADED=0
if ((RESUME)); then
  TAR="$R_TAR"; NAME="$(basename "$TAR")"
else
  NAME="$SLUG-$(date -u +%Y%m%dT%H%M%SZ).tar"; TAR="$STAGING/$NAME"
fi
# Until something has been uploaded nothing needs the tar; after that it is kept, for finishing or inspecting.
cleanup() { if ((UPLOADED == 0)) && [[ "$MODE" == all && "$RESUME" == 0 ]]; then rm -f "$TAR" "$TAR.part"; fi; }

echo "==> Preflight: R2 audit-archive reachable"
# An archive/ prefix with no objects yet is "directory not found" (exit 3), which is fine; anything else is not.
rc=0; as_audit "$RCLONE" lsf r2:audit-archive/archive --max-depth 1 --config "$CONF" >/dev/null || rc=$?
[[ $rc == 0 || $rc == 3 ]] || { echo "Cannot list r2:audit-archive/archive (exit $rc: token scope, network?)" >&2; exit 1; }

install -d -o audit -g audit -m 0750 "$STAGING"
trap cleanup EXIT

if ((RESUME)); then
  echo "==> 1/4 Package: using $TAR"
  [[ -f "$TAR" ]] || { echo "$TAR does not exist" >&2; exit 1; }
  tar_sha="${R_TAR_SHA,,}"; pkg_digest="${R_DIGEST,,}"
  [[ "$tar_sha" =~ ^[0-9a-f]{64}$ && "$pkg_digest" =~ ^[0-9a-f]{64}$ ]] || { echo "tar-sha256 and package-digest are 64 hex characters each" >&2; exit 1; }
else
  echo "==> 1/4 Package the engagement"
  out="$(audit-admin engagement package "$CODE" "$TAR")" || { echo "$out"; exit 1; }
  echo "$out"
  tar_sha="$(awk '$1 == "tar-sha256" { print $2 }' <<<"$out")"
  pkg_digest="$(awk '$1 == "package-digest" { print $2 }' <<<"$out")"
  [[ "$tar_sha" =~ ^[0-9a-f]{64}$ && "$pkg_digest" =~ ^[0-9a-f]{64}$ ]] || { echo "Could not read the digests from the package output" >&2; exit 1; }
fi
[[ "$(sha256sum "$TAR" | cut -d' ' -f1)" == "$tar_sha" ]] || { echo "The tar on disk does not hash to tar-sha256 $tar_sha" >&2; exit 1; }

if [[ "$MODE" == package ]]; then
  echo
  echo "Stopped before the upload (--package-only). Package: $TAR ($(du -h "$TAR" | cut -f1))"
  echo "Check it with:  tar -xf $TAR -C /tmp && (cd /tmp && sha256sum -c engagement-*/SHA256SUMS)"
  echo "To upload exactly this package:"
  echo "  sudo audit-engagement-archive '$CODE' --resume '$TAR' $tar_sha $pkg_digest"
  echo "(or delete it and run again without --package-only to build a new one)."
  exit 0
fi

REMOTE="archive:$SLUG/$NAME"
FP="$(/usr/local/sbin/audit-evidence-key --fingerprint)"
LOCATION="r2:audit-archive/archive/engagements/$SLUG/$NAME.bin (rclone crypt, evidence key fingerprint $FP)"
if [[ "$YES" != 1 ]]; then
  echo
  echo "About to write $NAME ($(du -h "$TAR" | cut -f1)) to audit-archive. It cannot be deleted or overwritten for 10 years."
  read -rp "Type the engagement code ($CODE) to continue: " answer
  [[ "$answer" == "$CODE" ]] || { echo "Cancelled; nothing was uploaded."; exit 1; }
fi

FINISH="audit-admin engagement archived '$CODE' $pkg_digest $tar_sha '$LOCATION'"
echo "==> 2/4 Upload (encrypted, never overwriting)"
# The crypt remote adds the .bin suffix itself, so the name here is the plain one.
[[ -z "$(as_audit "$RCLONE" lsf "$REMOTE" --config "$CONF" 2>/dev/null || true)" ]] || { echo "$REMOTE already exists; refusing" >&2; exit 1; }
UPLOADED=1
as_audit "$RCLONE" copyto "$TAR" "$REMOTE" --config "$CONF" --immutable --s3-upload-concurrency 4 --s3-chunk-size 16M \
  --retries 3 --low-level-retries 5 --log-level NOTICE --stats 0 \
  || { echo "The upload failed. An incomplete object may be in the bucket (it cannot be deleted); the tar is kept at $TAR. Find the cause before trying again." >&2; exit 1; }

echo "==> 3/4 Read it back from R2 and compare"
if ! remote_sha="$(as_audit "$RCLONE" cat "$REMOTE" --config "$CONF" | sha256sum | cut -d' ' -f1)"; then
  echo "FAIL: could not read the object back from R2 (network or decryption error)." >&2
  echo "The object IS in the bucket and stays there. The engagement is NOT marked archived. The tar is kept at $TAR." >&2
  exit 1
fi
if [[ "$remote_sha" != "$tar_sha" ]]; then
  echo "FAIL: the object in R2 hashes to $remote_sha, the package to $tar_sha." >&2
  echo "The object is locked and stays in the bucket. The engagement is NOT marked archived. The tar is kept at $TAR." >&2
  exit 1
fi
echo "R2 copy matches ($tar_sha)"

echo "==> 4/4 Mark the engagement archived"
if ! audit-admin engagement archived "$CODE" "$pkg_digest" "$tar_sha" "$LOCATION"; then
  echo >&2
  echo "The package is safely in the archive and verified, but the engagement could not be marked archived." >&2
  echo "Do NOT run everything again (that would upload a second permanent copy). Fix the cause, then finish with:" >&2
  echo "  sudo $FINISH" >&2
  echo "The tar is kept at $TAR." >&2
  exit 1
fi
UPLOADED=0   # finished: the tar is no longer needed
rm -f "$TAR" "$TAR.part"

echo
echo "Done. $CODE is Archived. Package digest $pkg_digest"
echo "Location: $LOCATION"
echo "Keep the evidence key offline (fingerprint $FP): without it the archive cannot be read."
