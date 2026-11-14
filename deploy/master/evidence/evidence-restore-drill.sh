#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-evidence-restore-drill; run monthly by audit-evidence-restore-drill.timer, or by hand.
# Proves that the mirror can be read back with the key on this VM: downloads up to $SAMPLE random objects from R2 into a
# scratch directory, decrypts them, and compares each with the copy on the VM byte for byte (sha256). The master's own
# check that every object still matches its BLAKE3 address is `sudo audit-admin evidence verify`.
# Mails PASS or FAIL through audit-backup-alert.
set -uo pipefail
SRC="${SRC:-/srv/audit/evidence}"
SAMPLE="${SAMPLE:-20}"
CONF=/etc/audit-evidence/rclone.conf
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
REPORT="$(mktemp)"; SCRATCH="$(mktemp -d /var/tmp/audit-evidence-drill.XXXXXX)"; chown audit "$SCRATCH"
FAILED=0
say() { echo "$*" | tee -a "$REPORT"; }
fail() { say "FAIL: $*"; FAILED=1; }
trap 'rm -rf "$SCRATCH"; rm -f "$REPORT"' EXIT

say "Evidence restore drill $(date -u +%FT%TZ) on $(hostname -s)"
mapfile -t files < <(find "$SRC" -type f -not -path "$SRC/.staging/*" | shuf -n "$SAMPLE")
total="$(find "$SRC" -type f -not -path "$SRC/.staging/*" | wc -l)"
say "Objects on the VM: $total; sampling ${#files[@]}"
checked=0
for f in "${files[@]}"; do
  rel="${f#"$SRC"/}"
  if ! runuser -u audit -- /usr/local/bin/rclone copyto "evidence:$rel" "$SCRATCH/$(basename "$rel")" --config "$CONF" --retries 3 --log-level ERROR 2>>"$REPORT"; then
    fail "$rel: cannot be downloaded or decrypted from R2"; continue
  fi
  if [[ "$(sha256sum < "$f")" == "$(sha256sum < "$SCRATCH/$(basename "$rel")")" ]]; then checked=$((checked + 1))
  else fail "$rel: downloaded copy differs from the copy on the VM"; fi
done
(( FAILED )) || say "  ok  $checked of ${#files[@]} sampled objects restored from R2 identical to the VM copy"
if (( total > 0 && ${#files[@]} == 0 )); then fail "could not sample objects"; fi

verdict=PASS; (( FAILED )) && verdict=FAIL
say "RESULT: $verdict"
/usr/local/sbin/audit-backup-alert "[audit-master] Diễn tập khôi phục bằng chứng: $verdict ($(hostname -s))" "$(cat "$REPORT")" || true
(( FAILED )) && exit 1 || exit 0
