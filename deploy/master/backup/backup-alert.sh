#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-backup-alert. Sends one alert e-mail through the Brevo HTTP API.
#   audit-backup-alert "Subject" "Body"        (body may also come on stdin)
#   audit-backup-alert --unit <systemd-unit>    (used by OnFailure=; attaches the unit's last log lines)
# Recipient: BACKUP_ALERT_EMAIL in /etc/audit-master/master.env (set with `sudo audit-set-secrets`).
# The message always goes to the journal too, so nothing is lost when mail is not configured.
set -uo pipefail
ENV_FILE="${ENV_FILE:-/etc/audit-master/master.env}"
getenv() { sed -n "s/^$1=//p" "$ENV_FILE" 2>/dev/null | tail -n1; }

if [[ "${1:-}" == "--unit" ]]; then
  unit="$2"
  subject="[audit-master] $unit thất bại trên $(hostname -s)"
  body="Unit $unit vừa thất bại lúc $(date -u +%FT%TZ).

$(journalctl -u "$unit" -n 60 --no-pager 2>&1)"
else
  subject="${1:?subject}"
  body="${2:-$(cat)}"
fi

logger -t audit-backup-alert -p user.err -- "$subject"
printf '%s\n%s\n' "$subject" "$body" >&2

to="$(getenv BACKUP_ALERT_EMAIL)"; key="$(getenv Mail__BrevoApiKey)"; from="$(getenv Mail__From)"
if [[ -z "$to" || -z "$key" ]]; then
  logger -t audit-backup-alert -p user.warning "BACKUP_ALERT_EMAIL or Mail__BrevoApiKey not set; alert written to the journal only"
  exit 0
fi
payload="$(jq -n --arg from "${from:-no-reply@audit.io.vn}" --arg to "$to" --arg s "$subject" --arg b "$body" \
  '{sender:{name:"audit-master backup", email:$from}, to:[{email:$to}], subject:$s, textContent:$b}')"
code=$(curl -s -o /tmp/audit-backup-alert.out -w '%{http_code}' --max-time 30 \
  -H "api-key: $key" -H 'content-type: application/json' -H 'accept: application/json' \
  --data "$payload" https://api.brevo.com/v3/smtp/email)
if [[ "$code" != 2* ]]; then
  logger -t audit-backup-alert -p user.err "Brevo returned HTTP $code: $(head -c 300 /tmp/audit-backup-alert.out)"
  exit 1
fi
