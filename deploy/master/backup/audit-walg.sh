#!/usr/bin/env bash
# Installed as /usr/local/bin/audit-walg. Runs WAL-G with the audit-master storage config.
# Used by PostgreSQL's archive_command / restore_command and by the backup scripts.
# WALG_CONFIG may point at another config (the monthly archive copy, or a test cluster).
exec /usr/local/bin/wal-g --config "${WALG_CONFIG:-/etc/wal-g/walg.json}" "$@"
