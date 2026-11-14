#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-evidence-config. (Re)writes the rclone config of the evidence mirror from the R2
# values in /etc/audit-master/master.env and the key in /etc/audit-evidence/crypt.key, so a rotated R2 token reaches
# the mirror too (audit-set-secrets does not call this; run it after rotating).
#   [r2]       R2 as an S3 endpoint
#   [evidence] client-side encryption (rclone crypt: NaCl secretbox) over r2:audit-live/evidence
# File names are not encrypted: they are BLAKE3 hashes of the content and reveal nothing. Contents are.
# The file is root:audit 0640 because the mirror runs rclone as the unprivileged `audit` user. Values are never printed.
set -euo pipefail
ENV_FILE="${ENV_FILE:-/etc/audit-master/master.env}"
OUT_DIR="${OUT_DIR:-/etc/audit-evidence}"
BUCKET_PATH="${BUCKET_PATH:-audit-live/evidence}"
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
getenv() { sed -n "s/^$1=//p" "$ENV_FILE" | tail -n1; }
acct="$(getenv R2_ACCOUNT_ID)"; akid="$(getenv R2_ACCESS_KEY_ID)"; secret="$(getenv R2_SECRET_ACCESS_KEY)"
[[ -n "$acct" && -n "$akid" && -n "$secret" ]] || { echo "R2 values missing in $ENV_FILE; run: sudo audit-set-secrets" >&2; exit 1; }
[[ -s "$OUT_DIR/crypt.key" ]] || { echo "No key in $OUT_DIR/crypt.key; run evidence-setup.sh first" >&2; exit 1; }

key="$(tr -d '[:space:]' < "$OUT_DIR/crypt.key")"
salt="$(printf '%s' "${key}audit-evidence-salt" | sha256sum | cut -d' ' -f1)"
tmp="$(mktemp "$OUT_DIR/.rclone.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
{
  echo "[r2]"
  echo "type = s3"
  echo "provider = Cloudflare"
  echo "access_key_id = $akid"
  echo "secret_access_key = $secret"
  echo "endpoint = https://${acct}.r2.cloudflarestorage.com"
  echo "no_check_bucket = true"
  echo
  echo "[evidence]"
  echo "type = crypt"
  echo "remote = r2:${BUCKET_PATH}"
  echo "filename_encryption = off"
  echo "directory_name_encryption = false"
  echo "password = $(printf '%s' "$key" | /usr/local/bin/rclone obscure -)"
  echo "password2 = $(printf '%s' "$salt" | /usr/local/bin/rclone obscure -)"
} > "$tmp"
chown root:audit "$tmp"; chmod 0640 "$tmp"; mv -f "$tmp" "$OUT_DIR/rclone.conf"; trap - EXIT
echo "rclone config written to $OUT_DIR/rclone.conf (remote: r2:${BUCKET_PATH}, contents encrypted)"
