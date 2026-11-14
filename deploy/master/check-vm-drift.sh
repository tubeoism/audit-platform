#!/usr/bin/env bash
# Compares the helper scripts installed on the VM with the ones in this repository.
# CD only ships the application; these scripts are installed by bootstrap-vm.sh, backup/backup-setup.sh and
# evidence/evidence-setup.sh, so they go stale unless someone re-runs the matching setup script on the VM.
#
#   deploy/master/check-vm-drift.sh <ssh-host>      # needs only a login that can read /usr/local/sbin (no sudo)
#
# Prints one DRIFT line per script that is missing or differs, and exits 1 if there is any (2 if the VM cannot be read).
# It only reads: nothing is installed or changed on the VM.
set -euo pipefail
HOST="${1:?usage: check-vm-drift.sh <ssh-host>}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# repo file : installed path : setup script that installs it
MAP=(
  "activate-release.sh:/usr/local/sbin/audit-activate-release:bootstrap-vm.sh"
  "set-secrets.sh:/usr/local/sbin/audit-set-secrets:bootstrap-vm.sh"
  "r2-lock-test.sh:/usr/local/sbin/audit-r2-lock-test:bootstrap-vm.sh"
  "auth-key.sh:/usr/local/sbin/audit-auth-key:bootstrap-vm.sh"
  "audit-admin.sh:/usr/local/sbin/audit-admin:bootstrap-vm.sh"
  "backup/audit-walg.sh:/usr/local/bin/audit-walg:backup/backup-setup.sh"
  "backup/walg-config.sh:/usr/local/sbin/audit-walg-config:backup/backup-setup.sh"
  "backup/walg-key.sh:/usr/local/sbin/audit-walg-key:backup/backup-setup.sh"
  "backup/pg-backup.sh:/usr/local/sbin/audit-pg-backup:backup/backup-setup.sh"
  "backup/pg-backup-check.sh:/usr/local/sbin/audit-pg-backup-check:backup/backup-setup.sh"
  "backup/pg-restore-drill.sh:/usr/local/sbin/audit-pg-restore-drill:backup/backup-setup.sh"
  "backup/backup-alert.sh:/usr/local/sbin/audit-backup-alert:backup/backup-setup.sh"
  "evidence/evidence-key.sh:/usr/local/sbin/audit-evidence-key:evidence/evidence-setup.sh"
  "evidence/evidence-config.sh:/usr/local/sbin/audit-evidence-config:evidence/evidence-setup.sh"
  "evidence/evidence-mirror.sh:/usr/local/sbin/audit-evidence-mirror:evidence/evidence-setup.sh"
  "evidence/evidence-mirror-check.sh:/usr/local/sbin/audit-evidence-mirror-check:evidence/evidence-setup.sh"
  "evidence/evidence-restore-drill.sh:/usr/local/sbin/audit-evidence-restore-drill:evidence/evidence-setup.sh"
  "evidence/engagement-archive.sh:/usr/local/sbin/audit-engagement-archive:evidence/evidence-setup.sh"
)

# The remote command always exits 0, so a non-zero status here means the VM could not be reached or read.
installed="$(ssh "$HOST" 'sha256sum /usr/local/sbin/audit-* /usr/local/bin/audit-walg 2>/dev/null || true')" \
  || { echo "SKIPPED: could not read the VM over ssh" >&2; exit 2; }

drift=0
for entry in "${MAP[@]}"; do
  IFS=: read -r src dst setup <<<"$entry"
  want="$(sha256sum "$HERE/$src" | cut -d' ' -f1)"
  have="$(awk -v p="$dst" '$2 == p { print $1 }' <<<"$installed")"
  if [[ -z "$have" ]]; then
    echo "DRIFT: $dst is not installed (run deploy/master/$setup on the VM)"; drift=1
  elif [[ "$have" != "$want" ]]; then
    echo "DRIFT: $dst differs from deploy/master/$src (re-run deploy/master/$setup on the VM)"; drift=1
  fi
done
[[ $drift -eq 0 ]] && echo "OK: ${#MAP[@]} helper scripts on the VM match the repository"
exit $drift
