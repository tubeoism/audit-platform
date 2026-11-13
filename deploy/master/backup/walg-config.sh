#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-walg-config. (Re)writes the WAL-G storage configs from the R2 values in
# /etc/audit-master/master.env, so a rotated R2 token reaches WAL-G too (audit-set-secrets calls this).
#   /etc/wal-g/walg.json          live backups: s3://audit-live/pg/audit-master
#   /etc/wal-g/walg-archive.json  monthly immutable copies: s3://audit-archive/archive/pg/audit-master
# Files are root:postgres 0640 because PostgreSQL's archive_command runs as postgres. Values are never printed.
set -euo pipefail
ENV_FILE="${ENV_FILE:-/etc/audit-master/master.env}"
LIVE_PREFIX="${LIVE_PREFIX:-s3://audit-live/pg/audit-master}"
ARCHIVE_PREFIX="${ARCHIVE_PREFIX:-s3://audit-archive/archive/pg/audit-master}"
OUT_DIR="${OUT_DIR:-/etc/wal-g}"
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
getenv() { sed -n "s/^$1=//p" "$ENV_FILE" | tail -n1; }
acct="$(getenv R2_ACCOUNT_ID)"; akid="$(getenv R2_ACCESS_KEY_ID)"; secret="$(getenv R2_SECRET_ACCESS_KEY)"
[[ -n "$acct" && -n "$akid" && -n "$secret" ]] || { echo "R2 values missing in $ENV_FILE; run: sudo audit-set-secrets" >&2; exit 1; }

install -d -o root -g postgres -m 0750 "$OUT_DIR"
write() {   # write FILE PREFIX
  local tmp; tmp="$(mktemp "$OUT_DIR/.walg.XXXXXX")"
  jq -n --arg prefix "$2" --arg ep "https://${acct}.r2.cloudflarestorage.com" --arg ak "$akid" --arg sk "$secret" \
        --arg key "$OUT_DIR/libsodium.key" '{
    WALG_S3_PREFIX: $prefix, AWS_ENDPOINT: $ep, AWS_REGION: "auto", AWS_S3_FORCE_PATH_STYLE: "true",
    AWS_ACCESS_KEY_ID: $ak, AWS_SECRET_ACCESS_KEY: $sk,
    WALG_LIBSODIUM_KEY_PATH: $key, WALG_LIBSODIUM_KEY_TRANSFORM: "hex",
    WALG_COMPRESSION_METHOD: "zstd", WALG_PREVENT_WAL_OVERWRITE: "true",
    WALG_UPLOAD_CONCURRENCY: "4", WALG_DOWNLOAD_CONCURRENCY: "4",
    PGHOST: "/var/run/postgresql", PGUSER: "postgres", PGDATABASE: "postgres"
  }' > "$tmp"
  chown root:postgres "$tmp"; chmod 0640 "$tmp"; mv -f "$tmp" "$1"
}
write "$OUT_DIR/walg.json" "$LIVE_PREFIX"
write "$OUT_DIR/walg-archive.json" "$ARCHIVE_PREFIX"
echo "WAL-G configs written to $OUT_DIR (live: $LIVE_PREFIX, archive: $ARCHIVE_PREFIX)"
