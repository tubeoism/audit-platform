#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-pg-backup; run daily by audit-pg-backup.timer.
#  1. Full base backup of the cluster to audit-live with WAL-G (compressed with zstd, encrypted with libsodium).
#  2. Retention: keep the newest $RETAIN_FULL full backups and the WAL they need (point-in-time window ~ RETAIN_FULL days).
#  3. On day $ARCHIVE_DAY of the month, copy the new backup and its WAL to audit-archive/archive/ (object lock: immutable).
# Any failure exits non-zero, and systemd's OnFailure= sends the alert.
set -euo pipefail
PGDATA="${PGDATA:-/var/lib/postgresql/16/main}"
PGPORT="${PGPORT:-5432}"
RETAIN_FULL="${RETAIN_FULL:-30}"
ARCHIVE_DAY="${ARCHIVE_DAY:-01}"
ARCHIVE_CONFIG="${ARCHIVE_CONFIG:-/etc/wal-g/walg-archive.json}"
export WALG_CONFIG="${WALG_CONFIG:-/etc/wal-g/walg.json}"

[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
pg() { runuser -u postgres -- env WALG_CONFIG="$WALG_CONFIG" PGPORT="$PGPORT" "$@"; }

echo "== backup-push $PGDATA (port $PGPORT)"
pg /usr/local/bin/audit-walg backup-push "$PGDATA"

latest="$(pg /usr/local/bin/audit-walg backup-list --json | jq -r 'max_by(.time) | .backup_name')"
echo "== latest backup: $latest"

echo "== retention: keep $RETAIN_FULL full backups"
pg /usr/local/bin/audit-walg delete --use-sentinel-time retain FULL "$RETAIN_FULL" --confirm

if [[ "${FORCE_ARCHIVE:-0}" == 1 || "$(date -u +%d)" == "$ARCHIVE_DAY" ]]; then
  if [[ -f "$ARCHIVE_CONFIG" ]]; then
    echo "== monthly copy of $latest to the locked archive"
    pg /usr/local/bin/wal-g copy --from "$WALG_CONFIG" --to "$ARCHIVE_CONFIG" --backup-name "$latest"
  else
    echo "== $ARCHIVE_CONFIG not found; skipping the monthly archive copy"
  fi
fi
echo "== done"
