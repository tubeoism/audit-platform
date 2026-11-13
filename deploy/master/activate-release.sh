#!/usr/bin/env bash
# Activates an uploaded release on the VM. Installed as /usr/local/sbin/audit-activate-release;
# the `deploy` user may run it (and only it) via sudo.
#
#   sudo audit-activate-release <release-id>      # migrate, switch, restart, health-check, rollback on failure
#   sudo audit-activate-release --rollback        # switch back to the previous release
#
# Expects /opt/audit-master/incoming/<release-id>.tar.gz containing app/ (published master) and efbundle.
set -euo pipefail

BASE=/opt/audit-master
RELEASES=$BASE/releases
CURRENT=$BASE/current
KEEP=5
HEALTH_URL=http://127.0.0.1:5080/healthz/ready

log() { printf '[activate] %s\n' "$*"; }
die() { log "ERROR: $*"; exit 1; }

health() {
  for _ in $(seq 1 30); do
    curl -fsS -o /dev/null "$HEALTH_URL" && return 0
    sleep 2
  done
  return 1
}

switch_to() {
  ln -sfn "$1" "$BASE/current.tmp" && mv -Tf "$BASE/current.tmp" "$CURRENT"
  systemctl restart audit-master.service
}

previous_release() {
  local cur; cur="$(readlink -f "$CURRENT" 2>/dev/null || true)"
  ls -1dt "$RELEASES"/*/ 2>/dev/null | sed 's:/$::' | grep -vx "$cur" | head -n1
}

if [[ "${1:-}" == "--rollback" ]]; then
  prev="$(previous_release)"; [[ -n "$prev" ]] || die "no previous release"
  log "rolling back to $(basename "$prev")"
  switch_to "$prev"; health || die "previous release is unhealthy too"
  log "rollback OK"; exit 0
fi

ID="${1:-}"
[[ "$ID" =~ ^[0-9a-f]{7,40}(-[0-9]+)?$ ]] || die "invalid release id '$ID'"
ARCHIVE="$BASE/incoming/$ID.tar.gz"
[[ -f "$ARCHIVE" ]] || die "missing $ARCHIVE"
TARGET="$RELEASES/$ID"

log "unpacking $ID"
rm -rf "$TARGET"; install -d -o root -g root -m 0755 "$TARGET"
tar -xzf "$ARCHIVE" -C "$TARGET" --no-same-owner
[[ -f "$TARGET/app/AuditPlatform.Master.dll" && -x "$TARGET/efbundle" ]] || die "archive layout unexpected"
chown -R root:root "$TARGET"; chmod -R go-w "$TARGET"
rm -f "$ARCHIVE"

log "applying migrations as schema owner"
# shellcheck disable=SC1091
set -a; . /etc/audit-master/migrate.env; set +a
"$TARGET/efbundle" --connection "$MIGRATE_CONNECTION"
unset MIGRATE_CONNECTION

log "granting application role (audit_log stays append-only for audit_app)"
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d audit_master <<'SQL'
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO audit_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO audit_app;
REVOKE UPDATE, DELETE, TRUNCATE ON audit_log FROM audit_app;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON "__EFMigrationsHistory" FROM audit_app;
SQL

prev="$(readlink -f "$CURRENT" 2>/dev/null || true)"
log "switching to $ID"
switch_to "$TARGET"
if ! health; then
  log "health check failed; journal tail:"
  journalctl -u audit-master.service -n 40 --no-pager || true
  if [[ -n "$prev" && -d "$prev" ]]; then
    log "restoring $(basename "$prev")"
    switch_to "$prev"; health || log "previous release also unhealthy"
  fi
  die "release $ID not activated"
fi
log "release $ID is live"

# Keep the newest $KEEP releases.
ls -1dt "$RELEASES"/*/ | tail -n +$((KEEP + 1)) | xargs -r rm -rf
