#!/usr/bin/env bash
# Interactive: asks for R2 and Brevo values and writes them into /etc/audit-master/master.env (root, 0600).
# Secrets are read without echo and never printed. Leave an answer empty to keep the current value.
# Run on the VM:  sudo audit-set-secrets
set -euo pipefail
ENV_FILE=/etc/audit-master/master.env
[[ $EUID -eq 0 ]] || { echo "Run with sudo" >&2; exit 1; }
[[ -f "$ENV_FILE" ]] || { echo "$ENV_FILE not found; run bootstrap-vm.sh first" >&2; exit 1; }

ask() {   # ask NAME "Prompt" secret|plain [default]
  local name=$1 prompt=$2 mode=$3 def=${4:-} val
  local cur; cur="$(sed -n "s/^${name}=//p" "$ENV_FILE" | tail -n1)"
  [[ -n "$cur" ]] && prompt="$prompt [đã có, Enter để giữ]" || { [[ -n "$def" ]] && prompt="$prompt [$def]"; }
  if [[ $mode == secret ]]; then read -rsp "$prompt: " val; echo; else read -rp "$prompt: " val; fi
  [[ -z "$val" ]] && val="${cur:-$def}"
  [[ "$val" == *$'\n'* ]] && { echo "Giá trị không hợp lệ" >&2; exit 1; }
  printf -v "NEW_$name" '%s' "$val"
  export "NEW_$name"
}

echo "== Cloudflare R2 =="
ask R2_ACCOUNT_ID        "R2 Account ID"         plain
ask R2_ACCESS_KEY_ID     "R2 Access Key ID"      plain
ask R2_SECRET_ACCESS_KEY "R2 Secret Access Key"  secret
echo "== Brevo SMTP =="
ask Mail__Host     "SMTP server"            plain  smtp-relay.brevo.com
ask Mail__Port     "SMTP port"              plain  587
ask Mail__Username "SMTP login (…@smtp-brevo.com)" plain
ask Mail__Password "SMTP key"               secret
ask Mail__From     "Địa chỉ gửi"            plain  no-reply@audit.io.vn

tmp="$(mktemp /etc/audit-master/.master.env.XXXXXX)"; chmod 0600 "$tmp"
NAMES="R2_ACCOUNT_ID R2_ACCESS_KEY_ID R2_SECRET_ACCESS_KEY Mail__Host Mail__Port Mail__Username Mail__Password Mail__From" \
awk '
  BEGIN { n = split(ENVIRON["NAMES"], k, " "); for (i = 1; i <= n; i++) want[k[i]] = 1 }
  { line = $0; sub(/^# */, "", line); split(line, kv, "=");
    if ((kv[1] in want) && ENVIRON["NEW_" kv[1]] != "") { if (!(kv[1] in done)) { print kv[1] "=" ENVIRON["NEW_" kv[1]]; done[kv[1]] = 1 }; next }
    print }
  END { for (x in want) if (!(x in done) && ENVIRON["NEW_" x] != "") print x "=" ENVIRON["NEW_" x] }
' "$ENV_FILE" > "$tmp"
mv -f "$tmp" "$ENV_FILE"; chown root:root "$ENV_FILE"; chmod 0600 "$ENV_FILE"

echo "Đã lưu vào $ENV_FILE. Khởi động lại master..."
systemctl restart audit-master.service
sleep 3
curl -fsS -o /dev/null http://127.0.0.1:5080/healthz/ready && echo "Master OK." || echo "Master chưa sẵn sàng, xem: journalctl -u audit-master -n 50"
