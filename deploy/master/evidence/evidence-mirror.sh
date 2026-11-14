#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-evidence-mirror; run every 10 minutes by audit-evidence-mirror.timer, or by hand.
# Copies new evidence objects from /srv/audit/evidence to the encrypted mirror on R2 (audit-live/evidence).
#   - `rclone copy`, never `sync`: nothing is ever deleted from R2 because it disappeared locally.
#   - --immutable: an object that already exists on R2 under the same name but with different content is an error,
#     not an overwrite. Objects are content-addressed, so that can only mean damage on one side.
#   - Objects are written to .staging and moved into place whole, so a half-written file is never copied; .staging is skipped.
# Success is recorded in /var/lib/audit-backup/evidence-mirror-ok for audit-evidence-mirror-check.
set -euo pipefail
SRC="${SRC:-/srv/audit/evidence}"
CONF=/etc/audit-evidence/rclone.conf
STATE=/var/lib/audit-backup
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
install -d -m 0700 "$STATE"
exec 9>"$STATE/evidence-mirror.lock"
flock -n 9 || { echo "A mirror run is already in progress"; exit 0; }

runuser -u audit -- /usr/local/bin/rclone copy "$SRC" evidence: --config "$CONF" \
  --exclude '/.staging/**' --immutable --size-only --transfers 4 --checkers 8 \
  --s3-upload-concurrency 4 --s3-chunk-size 16M --retries 3 --low-level-retries 5 \
  --log-level NOTICE --stats 0
date +%s > "$STATE/evidence-mirror-ok"
echo "evidence mirror OK: $(find "$SRC" -type f -not -path "$SRC/.staging/*" | wc -l) objects locally"
