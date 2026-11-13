#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-pg-backup-check; run hourly by audit-pg-backup-check.timer.
# Alerts (via audit-backup-alert) when:
#   - archive_mode is not on
#   - the last WAL archive attempt failed and nothing has been archived since
#   - a WAL segment has waited more than $WAL_MAX_MIN minutes to be archived
#   - the newest full backup is older than $FULL_MAX_H hours
# The same problem is re-sent at most every $REPEAT_H hours. Exit code 1 when any check fails.
set -uo pipefail
PGPORT="${PGPORT:-5432}"
WAL_MAX_MIN="${WAL_MAX_MIN:-15}"
FULL_MAX_H="${FULL_MAX_H:-26}"
REPEAT_H="${REPEAT_H:-6}"
STATE=/var/lib/audit-backup
export WALG_CONFIG="${WALG_CONFIG:-/etc/wal-g/walg.json}"
install -d -m 0700 "$STATE"

q() { runuser -u postgres -- psql -p "$PGPORT" -XAtq -d postgres -c "$1"; }
problems=()

[[ "$(q 'show archive_mode')" == on ]] || problems+=("archive_mode không bật: WAL không được sao lưu.")

failing="$(q "select coalesce(last_failed_time > coalesce(last_archived_time, 'epoch'), false) from pg_stat_archiver")"
[[ "$failing" == t ]] && problems+=("Lần đẩy WAL gần nhất thất bại: $(q "select last_failed_wal || ' lúc ' || last_failed_time from pg_stat_archiver")")

oldest_ready="$(q "select coalesce(round(extract(epoch from now() - min(modification))/60), 0)
                   from pg_ls_archive_statusdir() where name like '%.ready'")"
(( ${oldest_ready:-0} > WAL_MAX_MIN )) && problems+=("Có WAL chờ đẩy lên R2 đã ${oldest_ready} phút (ngưỡng ${WAL_MAX_MIN}).")

if list="$(runuser -u postgres -- env WALG_CONFIG="$WALG_CONFIG" /usr/local/bin/audit-walg backup-list --json 2>/tmp/audit-backup-check.err)"; then
  age_h="$(jq -r 'if length == 0 then "none" else ((now - (max_by(.time) | .time | sub("\\.[0-9]+";"") | fromdateiso8601)) / 3600 | floor) end' <<<"$list")"
  if [[ "$age_h" == none ]]; then problems+=("Chưa có base backup nào trên R2.")
  elif (( age_h > FULL_MAX_H )); then problems+=("Base backup mới nhất đã ${age_h} giờ (ngưỡng ${FULL_MAX_H}).")
  fi
else
  problems+=("Không đọc được danh sách backup từ R2: $(head -c 400 /tmp/audit-backup-check.err)")
fi

if (( ${#problems[@]} == 0 )); then
  echo "OK: archiving healthy, newest full backup ${age_h}h old"
  rm -f "$STATE/last-alert"
  exit 0
fi

msg="$(printf -- '- %s\n' "${problems[@]}")"
echo "$msg"
digest="$(sha256sum <<<"$msg" | cut -c1-16)"
if [[ -f "$STATE/last-alert" ]] && read -r last_digest last_ts < "$STATE/last-alert" \
   && [[ "$last_digest" == "$digest" ]] && (( $(date +%s) - last_ts < REPEAT_H * 3600 )); then
  echo "(same alert already sent within ${REPEAT_H}h)"
else
  /usr/local/sbin/audit-backup-alert "[audit-master] Sao lưu PostgreSQL có vấn đề trên $(hostname -s)" \
"$msg
Kiểm tra: sudo audit-pg-backup-check ; journalctl -u postgresql@16-main -u audit-pg-backup -n 100
Runbook: deploy/master/backup/RESTORE.md" && echo "$digest $(date +%s)" > "$STATE/last-alert"
fi
exit 1
