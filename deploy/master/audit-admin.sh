#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-admin. Account administration from the VM console (ADR-0005): the way the first
# administrator is invited, and the way back in if every administrator is locked out.
#   sudo audit-admin invite an@audit.io.vn "Nguyen Van An" --admin
#   sudo audit-admin list
#   sudo audit-admin            (prints all commands)
# Runs the live release as the unprivileged `audit` user with the service's own environment and the same sandbox as
# audit-master.service (ProtectSystem=strict, only /srv/audit writable, so `evidence verify` can open the evidence store). Every action is written
# to the audit log with the name of the person who ran sudo.
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run with sudo" >&2; exit 1; }
APP=/opt/audit-master/current/app
[[ -f "$APP/AuditPlatform.Master.dll" ]] || { echo "No release is live yet; run the Deploy master workflow first" >&2; exit 1; }

exec systemd-run --quiet --wait --pipe --collect \
  -p User=audit -p Group=audit \
  -p EnvironmentFile=/etc/audit-master/master.env \
  -p WorkingDirectory="$APP" \
  -p NoNewPrivileges=true -p ProtectSystem=strict -p ReadWritePaths=/srv/audit -p ProtectHome=true -p PrivateTmp=true \
  -E ASPNETCORE_ENVIRONMENT=Production -E DOTNET_gcServer=0 -E DOTNET_NOLOGO=1 \
  -E AUDIT_ADMIN_OPERATOR="${SUDO_USER:-root}" \
  /usr/bin/dotnet "$APP/AuditPlatform.Master.dll" admin "$@"
