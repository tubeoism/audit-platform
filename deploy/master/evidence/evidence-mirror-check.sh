#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-evidence-mirror-check; run hourly by audit-evidence-mirror-check.timer.
# Alerts (via audit-backup-alert) when:
#   - the mirror has not completed a run for more than $MAX_AGE_H hours
#   - an object older than $MIN_AGE is on the VM but missing from R2 or of a different size (rclone check, one way)
# The same problem is re-sent at most every $REPEAT_H hours. Exit code 1 when any check fails.
set -uo pipefail
SRC="${SRC:-/srv/audit/evidence}"
CONF=/etc/audit-evidence/rclone.conf
STATE=/var/lib/audit-backup
MAX_AGE_H="${MAX_AGE_H:-2}"
MIN_AGE="${MIN_AGE:-30m}"
REPEAT_H="${REPEAT_H:-6}"
install -d -m 0700 "$STATE"
problems=()

ok_ts="$(cat "$STATE/evidence-mirror-ok" 2>/dev/null || echo 0)"
age_h=$(( ($(date +%s) - ok_ts) / 3600 ))
if (( ok_ts == 0 )); then problems+=("Bản sao bằng chứng chưa từng chạy xong lần nào.")
elif (( age_h > MAX_AGE_H )); then problems+=("Bản sao bằng chứng lần cuối chạy xong cách đây ${age_h} giờ (ngưỡng ${MAX_AGE_H}).")
fi

missing="$(runuser -u audit -- /usr/local/bin/rclone check "$SRC" evidence: --config "$CONF" --one-way --size-only \
  --exclude '/.staging/**' --min-age "$MIN_AGE" --combined - 2>/tmp/audit-evidence-check.err | grep -E '^[-*!] ' | head -n 20 || true)"
if [[ -n "$missing" ]]; then
  problems+=("Có bằng chứng trên VM mà R2 thiếu hoặc khác kích thước (tối đa 20 dòng; '-' thiếu, '*' khác, '!' lỗi):
$missing")
elif grep -qiE 'failed|error' /tmp/audit-evidence-check.err 2>/dev/null; then
  problems+=("Không đối chiếu được với R2: $(head -c 400 /tmp/audit-evidence-check.err)")
fi

if (( ${#problems[@]} == 0 )); then
  echo "OK: evidence mirror complete, last run ${age_h}h ago"
  rm -f "$STATE/evidence-last-alert"
  exit 0
fi

msg="$(printf -- '- %s\n' "${problems[@]}")"
echo "$msg"
digest="$(sha256sum <<<"$msg" | cut -c1-16)"
if [[ -f "$STATE/evidence-last-alert" ]] && read -r last_digest last_ts < "$STATE/evidence-last-alert" \
   && [[ "$last_digest" == "$digest" ]] && (( $(date +%s) - last_ts < REPEAT_H * 3600 )); then
  echo "(same alert already sent within ${REPEAT_H}h)"
else
  /usr/local/sbin/audit-backup-alert "[audit-master] Bản sao kho bằng chứng có vấn đề trên $(hostname -s)" \
"$msg
Kiểm tra: sudo audit-evidence-mirror-check ; journalctl -u audit-evidence-mirror -n 100
Runbook: deploy/master/backup/RESTORE.md (phần kho bằng chứng)" && echo "$digest $(date +%s)" > "$STATE/evidence-last-alert"
fi
exit 1
