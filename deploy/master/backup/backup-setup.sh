#!/usr/bin/env bash
# Sets up WAL-G backups of the master's PostgreSQL to Cloudflare R2. Idempotent; never prints secrets.
# Run on the VM as root from a copy of deploy/master/:
#   sudo bash backup/backup-setup.sh            install WAL-G, key, configs, scripts and units (no PostgreSQL change)
#   sudo bash backup/backup-setup.sh --enable   also turn on WAL archiving (restarts PostgreSQL: a few seconds of
#                                               downtime), take the first base backup and start the timers
# Needs R2 values in /etc/audit-master/master.env (sudo audit-set-secrets).
set -euo pipefail
WALG_VERSION=v3.0.9
WALG_ASSET=wal-g-pg-24.04-aarch64.tar.gz
WALG_SHA256=cb37c5f8391bd8ef2efceeb9c6995059c0abf2fa74b9b717b42a16a7c93cfde1
HERE="$(cd "$(dirname "$0")" && pwd)"
PGCONF_D=/etc/postgresql/16/main/conf.d
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
[[ "$(uname -m)" == aarch64 ]] || { echo "This pins the aarch64 build of WAL-G; adjust WALG_ASSET/WALG_SHA256" >&2; exit 1; }
log() { printf '\n==> %s\n' "$*"; }

log "WAL-G $WALG_VERSION"
if ! /usr/local/bin/wal-g --version 2>/dev/null | grep -q "$WALG_VERSION"; then
  tmp="$(mktemp -d)"
  curl -fsSL -o "$tmp/$WALG_ASSET" "https://github.com/wal-g/wal-g/releases/download/$WALG_VERSION/$WALG_ASSET"
  echo "$WALG_SHA256  $tmp/$WALG_ASSET" | sha256sum -c --quiet
  tar -xzf "$tmp/$WALG_ASSET" -C "$tmp"
  install -o root -g root -m 0755 "$tmp/wal-g-pg-24.04-aarch64" /usr/local/bin/wal-g
  rm -rf "$tmp"
fi
/usr/local/bin/wal-g --version | head -n1

log "Scripts"
install -o root -g root -m 0755 "$HERE/audit-walg.sh"        /usr/local/bin/audit-walg
install -o root -g root -m 0755 "$HERE/walg-config.sh"       /usr/local/sbin/audit-walg-config
install -o root -g root -m 0755 "$HERE/walg-key.sh"          /usr/local/sbin/audit-walg-key
install -o root -g root -m 0755 "$HERE/pg-backup.sh"         /usr/local/sbin/audit-pg-backup
install -o root -g root -m 0755 "$HERE/pg-backup-check.sh"   /usr/local/sbin/audit-pg-backup-check
install -o root -g root -m 0755 "$HERE/pg-restore-drill.sh"  /usr/local/sbin/audit-pg-restore-drill
install -o root -g root -m 0755 "$HERE/backup-alert.sh"      /usr/local/sbin/audit-backup-alert

log "Encryption key (libsodium, 32 random bytes; generated once, never printed)"
install -d -o root -g postgres -m 0750 /etc/wal-g
if [[ ! -s /etc/wal-g/libsodium.key ]]; then
  (umask 027; openssl rand -hex 32 > /etc/wal-g/libsodium.key)
  chown root:postgres /etc/wal-g/libsodium.key; chmod 0640 /etc/wal-g/libsodium.key
  echo "New key generated. Store an offline copy NOW:  sudo audit-walg-key --show"
fi
echo "Key fingerprint: $(/usr/local/sbin/audit-walg-key --fingerprint)"

log "Storage configs"
/usr/local/sbin/audit-walg-config
runuser -u postgres -- /usr/local/bin/audit-walg st ls / >/dev/null && echo "R2 audit-live reachable"

log "systemd units"
for u in "$HERE"/systemd/*; do install -o root -g root -m 0644 "$u" /etc/systemd/system/; done
systemctl daemon-reload

if [[ "${1:-}" != "--enable" ]]; then
  echo; echo "Installed. Archiving is NOT on yet. Run again with --enable to restart PostgreSQL with archiving."
  exit 0
fi

log "PostgreSQL: WAL archiving (restart)"
install -o root -g root -m 0644 "$HERE/50-walg.conf" "$PGCONF_D/50-walg.conf"
if [[ "$(sudo -u postgres psql -XAtc 'show archive_mode')" != on ]]; then
  systemctl restart postgresql@16-main
  for _ in $(seq 1 30); do sudo -u postgres pg_isready -q && break; sleep 1; done
else
  systemctl reload postgresql@16-main
fi
sudo -u postgres psql -XAtc "select 'archive_mode=' || current_setting('archive_mode') || ', archive_timeout=' || current_setting('archive_timeout')"
if ! curl -fsS -o /dev/null http://127.0.0.1:5080/healthz/ready; then
  systemctl restart audit-master; sleep 3
  curl -fsS -o /dev/null http://127.0.0.1:5080/healthz/ready && echo "Master OK" || echo "WARNING: master not ready: journalctl -u audit-master -n 50"
else echo "Master OK"; fi

log "First WAL push and base backup"
sudo -u postgres psql -XAtc "select pg_walfile_name(pg_switch_wal())"
/usr/local/sbin/audit-pg-backup

log "Timers"
systemctl enable --now audit-pg-backup.timer audit-pg-backup-check.timer audit-pg-restore-drill.timer
systemctl list-timers 'audit-*' --no-pager
cat <<EOF

Done. Still to do by hand:
  1. Store an offline copy of the backup key:  sudo audit-walg-key --show   (fingerprint $(/usr/local/sbin/audit-walg-key --fingerprint))
  2. Set the alert recipient if not yet set:   sudo audit-set-secrets
  3. Run the first restore drill:               sudo audit-pg-restore-drill
EOF
