#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-pg-restore-drill; run monthly by audit-pg-restore-drill.timer, or by hand.
# Restores the newest base backup from R2 plus all archived WAL into a scratch instance on port $DRILL_PORT,
# then checks it against the live database:
#   - every table in schema public has at least the row count taken just before the drill (and no more than now)
#   - the audit_log row at the snapshot's last sequence has the same hash, and the chain links are unbroken
#   - the append-only triggers on audit_log exist
# The scratch instance never archives (archive_mode=off) and is deleted afterwards unless --keep is given.
#
#   sudo audit-pg-restore-drill                          drill against the latest backup, then clean up
#   sudo audit-pg-restore-drill --keep                   leave the scratch instance running for inspection
#   sudo audit-pg-restore-drill --target-time '2026-11-20 08:15:00+07' --keep
#                                                        point-in-time restore (runbook scenario A); skips the checks
#   sudo audit-pg-restore-drill --cleanup                stop and delete a kept scratch instance
set -euo pipefail
PGPORT="${PGPORT:-5432}"
DB="${DB:-audit_master}"
DRILL_PORT="${DRILL_PORT:-5433}"
DRILL_DIR="${DRILL_DIR:-/var/lib/postgresql/restore-drill}"
BIN=/usr/lib/postgresql/16/bin
REPORT_TO_MAIL="${REPORT_TO_MAIL:-1}"
export WALG_CONFIG="${WALG_CONFIG:-/etc/wal-g/walg.json}"

KEEP=0; TARGET_TIME=""; BACKUP=LATEST
while (( $# )); do
  case "$1" in
    --keep) KEEP=1 ;;
    --target-time) TARGET_TIME="$2"; shift ;;
    --backup) BACKUP="$2"; shift ;;
    --cleanup) CLEANUP_ONLY=1 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac; shift
done
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }

as_pg() { runuser -u postgres -- env WALG_CONFIG="$WALG_CONFIG" "$@"; }
live() { as_pg psql -p "$PGPORT" -XAtq -d "$DB" -v ON_ERROR_STOP=1 -c "$1"; }
drill() { as_pg psql -h "$DRILL_DIR" -p "$DRILL_PORT" -XAtq -d "$DB" -v ON_ERROR_STOP=1 -c "$1"; }
stop_drill() { [[ -f "$DRILL_DIR/postmaster.pid" ]] && as_pg "$BIN/pg_ctl" -D "$DRILL_DIR" -m fast -w stop >/dev/null 2>&1 || true; }
cleanup() { stop_drill; rm -rf --one-file-system "$DRILL_DIR"; }

if [[ "${CLEANUP_ONLY:-0}" == 1 ]]; then cleanup; echo "Scratch instance removed."; exit 0; fi

REPORT="$(mktemp)"; FAILED=0
say() { echo "$*" | tee -a "$REPORT"; }
fail() { say "FAIL: $*"; FAILED=1; }
finish() {
  local rc=$?
  (( KEEP )) || cleanup
  (( rc != 0 && FAILED == 0 )) && { say "FAIL: drill aborted (exit $rc), see journalctl -u audit-pg-restore-drill"; FAILED=1; }
  if [[ -z "$TARGET_TIME" && "$REPORT_TO_MAIL" == 1 ]]; then
    local verdict=PASS; (( FAILED )) && verdict=FAIL
    /usr/local/sbin/audit-backup-alert "[audit-master] Diễn tập khôi phục: $verdict ($(hostname -s))" "$(cat "$REPORT")" || true
  fi
  rm -f "$REPORT"
  (( FAILED )) && exit 1 || exit 0
}
trap finish EXIT

say "Restore drill $(date -u +%FT%TZ) on $(hostname -s), backup $BACKUP${TARGET_TIME:+, target $TARGET_TIME}"
[[ -e "$DRILL_DIR" ]] && { say "Removing leftover $DRILL_DIR"; cleanup; }

if [[ -z "$TARGET_TIME" ]]; then
  # Snapshot of the live database, then force the current WAL segment to the archive so the restore can reach it.
  TABLES="$(live "select string_agg(quote_ident(table_name), ' ' order by table_name) from information_schema.tables
                  where table_schema='public' and table_type='BASE TABLE'")"
  declare -A SNAP
  for t in $TABLES; do SNAP[$t]="$(live "select count(*) from public.$t")"; done
  SNAP_SEQ="$(live "select coalesce(max(sequence), -1) from audit_log")"
  SNAP_HASH="$(live "select coalesce((select hash from audit_log where sequence = $SNAP_SEQ), '')")"
  wal="$(live "select pg_walfile_name(pg_switch_wal())")"
  say "Live snapshot: audit_log max sequence $SNAP_SEQ; switched WAL at $wal"
  for _ in $(seq 1 60); do
    [[ "$(live "select coalesce(last_archived_wal >= '$wal', false) from pg_stat_archiver")" == t ]] && break; sleep 2
  done
  [[ "$(live "select coalesce(last_archived_wal >= '$wal', false) from pg_stat_archiver")" == t ]] \
    || { fail "segment $wal was not archived within 120 s"; exit 1; }
fi

t0=$(date +%s)
install -d -o postgres -g postgres -m 0700 "$DRILL_DIR"
as_pg /usr/local/bin/audit-walg backup-fetch "$DRILL_DIR" "$BACKUP"
t_fetch=$(date +%s)

# Ubuntu keeps postgresql.conf and pg_hba.conf in /etc, so the restored data directory needs its own minimal ones.
as_pg bash -c "cat > '$DRILL_DIR/postgresql.conf'" <<EOF
port = $DRILL_PORT
listen_addresses = ''
unix_socket_directories = '$DRILL_DIR'
shared_buffers = 256MB
archive_mode = off
restore_command = 'WALG_CONFIG=$WALG_CONFIG /usr/local/bin/audit-walg wal-fetch %f %p'
recovery_target_timeline = 'latest'
${TARGET_TIME:+recovery_target_time = '$TARGET_TIME'}
${TARGET_TIME:+recovery_target_action = 'pause'}
EOF
as_pg bash -c "printf 'local all postgres peer\n' > '$DRILL_DIR/pg_hba.conf'; : > '$DRILL_DIR/pg_ident.conf'; : > '$DRILL_DIR/postgresql.auto.conf'; touch '$DRILL_DIR/recovery.signal'"

as_pg "$BIN/pg_ctl" -D "$DRILL_DIR" -l "$DRILL_DIR/drill.log" -t 1800 -w start >/dev/null
if [[ -z "$TARGET_TIME" ]]; then
  for _ in $(seq 1 900); do [[ "$(drill 'select pg_is_in_recovery()' 2>/dev/null)" == f ]] && break; sleep 2; done
  [[ "$(drill 'select pg_is_in_recovery()')" == f ]] || { fail "recovery did not finish"; tail -n 30 "$DRILL_DIR/drill.log" >> "$REPORT"; exit 1; }
else
  for _ in $(seq 1 900); do [[ "$(drill 'select pg_is_wal_replay_paused()' 2>/dev/null)" == t ]] && break; sleep 2; done
fi
t_ready=$(date +%s)
say "Restored in $((t_ready - t0)) s (fetch $((t_fetch - t0)) s, WAL replay $((t_ready - t_fetch)) s)"
say "Last replayed transaction: $(drill 'select coalesce(pg_last_xact_replay_timestamp()::text, $$none$$)')"

if [[ -n "$TARGET_TIME" ]]; then
  say "Point-in-time instance is paused at the target and left running:"
  say "  sudo -u postgres psql -h $DRILL_DIR -p $DRILL_PORT -d $DB"
  say "When done: sudo audit-pg-restore-drill --cleanup"
  KEEP=1; exit 0
fi

for t in $TABLES; do
  d="$(drill "select count(*) from public.$t")"; now="$(live "select count(*) from public.$t")"
  if (( d < ${SNAP[$t]} || d > now )); then fail "$t: restored $d rows, snapshot ${SNAP[$t]}, live now $now"
  else say "  ok  $t: $d rows (snapshot ${SNAP[$t]})"; fi
done
if (( SNAP_SEQ >= 0 )); then
  h="$(drill "select coalesce((select hash from audit_log where sequence = $SNAP_SEQ), '')")"
  [[ "$h" == "$SNAP_HASH" ]] && say "  ok  audit_log hash at sequence $SNAP_SEQ matches live" || fail "audit_log hash at $SNAP_SEQ differs"
fi
breaks="$(drill "select count(*) from (select sequence, previous_hash, lag(hash) over (order by sequence) as prev,
                 row_number() over (order by sequence) as rn from audit_log) x
                 where (rn = 1 and previous_hash <> repeat('0', 64)) or (rn > 1 and previous_hash <> prev)")"
[[ "$breaks" == 0 ]] && say "  ok  audit_log chain links intact" || fail "audit_log has $breaks broken chain links"
trg="$(drill "select count(*) from pg_trigger where tgrelid = 'audit_log'::regclass and not tgisinternal")"
(( trg >= 2 )) && say "  ok  audit_log append-only triggers present ($trg)" || fail "audit_log triggers missing (found $trg)"

(( FAILED )) && say "RESULT: FAIL" || say "RESULT: PASS (RTO for the database: $((t_ready - t0)) s)"
