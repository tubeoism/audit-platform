#!/usr/bin/env bash
# Sets up the encrypted off-site mirror of the evidence store (ADR-0003, ADR-0004 "Evidence"). Idempotent; never prints secrets.
# Run on the VM as root from a copy of deploy/master/:
#   sudo bash evidence/evidence-setup.sh            install rclone, key, config, scripts and units (nothing is copied yet)
#   sudo bash evidence/evidence-setup.sh --enable   also copy what exists now and start the timers
# Needs R2 values in /etc/audit-master/master.env (sudo audit-set-secrets) and the alert mail from backup-setup.sh.
set -euo pipefail
RCLONE_VERSION=v1.76.0
RCLONE_ASSET=rclone-$RCLONE_VERSION-linux-arm64.zip
RCLONE_SHA256=ee169016675e23a69d77240565133ff7dbc9b33d16fa8f7053d4af39f1eaeb78
HERE="$(cd "$(dirname "$0")" && pwd)"
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
[[ "$(uname -m)" == aarch64 ]] || { echo "This pins the arm64 build of rclone; adjust RCLONE_ASSET/RCLONE_SHA256" >&2; exit 1; }
[[ -x /usr/local/sbin/audit-backup-alert ]] || { echo "Run backup/backup-setup.sh first (alert mail)" >&2; exit 1; }
log() { printf '\n==> %s\n' "$*"; }

log "rclone $RCLONE_VERSION"
if ! /usr/local/bin/rclone version 2>/dev/null | head -n1 | grep -q "$RCLONE_VERSION"; then
  command -v unzip >/dev/null || apt-get install -y unzip
  tmp="$(mktemp -d)"
  curl -fsSL -o "$tmp/$RCLONE_ASSET" "https://github.com/rclone/rclone/releases/download/$RCLONE_VERSION/$RCLONE_ASSET"
  echo "$RCLONE_SHA256  $tmp/$RCLONE_ASSET" | sha256sum -c --quiet
  unzip -q -o "$tmp/$RCLONE_ASSET" -d "$tmp"
  install -o root -g root -m 0755 "$tmp/rclone-$RCLONE_VERSION-linux-arm64/rclone" /usr/local/bin/rclone
  rm -rf "$tmp"
fi
/usr/local/bin/rclone version | head -n1

log "Scripts"
install -o root -g root -m 0755 "$HERE/evidence-key.sh"           /usr/local/sbin/audit-evidence-key
install -o root -g root -m 0755 "$HERE/evidence-config.sh"        /usr/local/sbin/audit-evidence-config
install -o root -g root -m 0755 "$HERE/evidence-mirror.sh"        /usr/local/sbin/audit-evidence-mirror
install -o root -g root -m 0755 "$HERE/evidence-mirror-check.sh"  /usr/local/sbin/audit-evidence-mirror-check
install -o root -g root -m 0755 "$HERE/evidence-restore-drill.sh" /usr/local/sbin/audit-evidence-restore-drill
install -o root -g root -m 0755 "$HERE/engagement-archive.sh"      /usr/local/sbin/audit-engagement-archive

log "Encryption key (32 random bytes; generated once, never printed)"
install -d -o root -g audit -m 0750 /etc/audit-evidence
# Where audit-engagement-archive builds a package before it is uploaded (audit-admin runs as `audit` and creates no folders).
install -d -o audit -g audit -m 0750 /srv/audit/archive-staging
if [[ ! -s /etc/audit-evidence/crypt.key ]]; then
  (umask 027; openssl rand -hex 32 > /etc/audit-evidence/crypt.key)
  chown root:audit /etc/audit-evidence/crypt.key; chmod 0640 /etc/audit-evidence/crypt.key
  echo "New key generated. Store an offline copy NOW:  sudo audit-evidence-key --show"
fi
echo "Key fingerprint: $(/usr/local/sbin/audit-evidence-key --fingerprint)"

log "rclone config and a test of R2"
/usr/local/sbin/audit-evidence-config
runuser -u audit -- /usr/local/bin/rclone lsd r2:audit-live --config /etc/audit-evidence/rclone.conf >/dev/null && echo "R2 audit-live reachable"

log "systemd units"
for u in "$HERE"/systemd/*; do install -o root -g root -m 0644 "$u" /etc/systemd/system/; done
systemctl daemon-reload

if [[ "${1:-}" != "--enable" ]]; then
  echo; echo "Installed. Nothing is copied yet. Store the key offline, then run again with --enable."
  exit 0
fi

log "First copy (may take a while once there is evidence)"
/usr/local/sbin/audit-evidence-mirror

log "Timers"
systemctl enable --now audit-evidence-mirror.timer audit-evidence-mirror-check.timer audit-evidence-restore-drill.timer
systemctl list-timers 'audit-evidence-*' --no-pager
cat <<EOF2

Done. Still to do by hand:
  1. Store an offline copy of the evidence key:  sudo audit-evidence-key --show   (fingerprint $(/usr/local/sbin/audit-evidence-key --fingerprint))
  2. Run the first restore drill once there is at least one evidence file:  sudo audit-evidence-restore-drill
EOF2
