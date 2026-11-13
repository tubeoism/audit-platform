#!/usr/bin/env bash
# One-time (idempotent) setup of the Oracle Cloud Arm VM for the master service.
# Run as root on the VM:   sudo bash bootstrap-vm.sh
# Target: Ubuntu 24.04 LTS aarch64. Safe to re-run; it never overwrites existing secrets.
set -euo pipefail

DOMAIN="${DOMAIN:-app.audit.io.vn}"
DB_NAME="audit_master"
OWNER_ROLE="audit_owner"   # owns the schema, runs migrations
APP_ROLE="audit_app"       # used by the service; INSERT/SELECT only on audit_log
HERE="$(cd "$(dirname "$0")" && pwd)"

[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
. /etc/os-release
[[ "$ID" == "ubuntu" ]] || { echo "Expected Ubuntu, found $ID" >&2; exit 1; }

log() { printf '\n==> %s\n' "$*"; }

log "Packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -yq ca-certificates curl gnupg debian-keyring debian-archive-keyring apt-transport-https \
  unattended-upgrades postgresql-16 postgresql-contrib iptables-persistent jq
# ASP.NET Core 10 runtime (Ubuntu feed); fall back to Microsoft feed if not yet in the distro archive.
if ! apt-get install -yq aspnetcore-runtime-10.0; then
  curl -fsSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/msprod.deb
  dpkg -i /tmp/msprod.deb && apt-get update -q && apt-get install -yq aspnetcore-runtime-10.0
fi
# Caddy (reverse proxy + automatic TLS)
if ! command -v caddy >/dev/null; then
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -q && apt-get install -yq caddy
fi
dpkg-reconfigure -f noninteractive unattended-upgrades

log "Users and directories"
id audit  >/dev/null 2>&1 || useradd --system --home /opt/audit-master --shell /usr/sbin/nologin audit
id deploy >/dev/null 2>&1 || useradd --create-home --shell /bin/bash deploy
install -d -o deploy -g deploy -m 0755 /opt/audit-master /opt/audit-master/releases /opt/audit-master/incoming
install -d -o audit  -g audit  -m 0750 /srv/audit /srv/audit/evidence
install -d -o root   -g root   -m 0700 /etc/audit-master
install -d -o deploy -g deploy -m 0700 /home/deploy/.ssh
touch /home/deploy/.ssh/authorized_keys
chown deploy:deploy /home/deploy/.ssh/authorized_keys && chmod 0600 /home/deploy/.ssh/authorized_keys

log "Activation script and sudo rule (deploy may run only this script as root)"
install -o root -g root -m 0755 "$HERE/activate-release.sh" /usr/local/sbin/audit-activate-release
install -o root -g root -m 0755 "$HERE/set-secrets.sh" /usr/local/sbin/audit-set-secrets
cat > /etc/sudoers.d/audit-deploy <<'EOF'
deploy ALL=(root) NOPASSWD: /usr/local/sbin/audit-activate-release *
EOF
chmod 0440 /etc/sudoers.d/audit-deploy
visudo -cf /etc/sudoers.d/audit-deploy

log "PostgreSQL: roles and database (passwords generated once, stored root-only)"
gen() { openssl rand -base64 33 | tr -d '/+=' | cut -c1-40; }
if [[ ! -f /etc/audit-master/migrate.env ]]; then
  OWNER_PW="$(gen)"; APP_PW="$(gen)"
  sudo -u postgres psql -v ON_ERROR_STOP=1 -q <<SQL
DO \$\$ BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${OWNER_ROLE}') THEN CREATE ROLE ${OWNER_ROLE} LOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '${APP_ROLE}')   THEN CREATE ROLE ${APP_ROLE} LOGIN;   END IF;
END \$\$;
ALTER ROLE ${OWNER_ROLE} PASSWORD '${OWNER_PW}';
ALTER ROLE ${APP_ROLE}   PASSWORD '${APP_PW}';
SQL
  sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='${DB_NAME}'" | grep -q 1 \
    || sudo -u postgres createdb -O "${OWNER_ROLE}" "${DB_NAME}"
  sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d "${DB_NAME}" <<SQL
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO ${APP_ROLE};
SQL
  umask 077
  printf 'MIGRATE_CONNECTION=Host=127.0.0.1;Port=5432;Database=%s;Username=%s;Password=%s\n' \
    "$DB_NAME" "$OWNER_ROLE" "$OWNER_PW" > /etc/audit-master/migrate.env
  if [[ ! -f /etc/audit-master/master.env ]]; then
    sed "s|^ConnectionStrings__Master=.*|ConnectionStrings__Master=Host=127.0.0.1;Port=5432;Database=${DB_NAME};Username=${APP_ROLE};Password=${APP_PW}|" \
      "$HERE/master.env.example" > /etc/audit-master/master.env
  fi
  chmod 0600 /etc/audit-master/*.env
  unset OWNER_PW APP_PW
fi
# PostgreSQL listens on localhost only (Ubuntu default); enforce it.
PGCONF=/etc/postgresql/16/main/postgresql.conf
grep -q "^listen_addresses = 'localhost'" "$PGCONF" || { echo "listen_addresses = 'localhost'" >> "$PGCONF"; systemctl restart postgresql; }

log "systemd unit and Caddy"
install -o root -g root -m 0644 "$HERE/audit-master.service" /etc/systemd/system/audit-master.service
systemctl daemon-reload
systemctl enable audit-master.service >/dev/null
install -d -o caddy -g caddy -m 0755 /var/log/caddy
sed "s/{\$DOMAIN}/${DOMAIN}/" "$HERE/Caddyfile" > /etc/caddy/Caddyfile
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
chown -R caddy:caddy /var/log/caddy   # validate runs as root and may create the log file
systemctl enable --now caddy && systemctl reload caddy

log "Host firewall: allow 80/443 (Oracle images ship iptables with REJECT-all)"
for p in 80 443; do
  iptables -C INPUT -p tcp --dport "$p" -m state --state NEW -j ACCEPT 2>/dev/null \
    || iptables -I INPUT 5 -p tcp --dport "$p" -m state --state NEW -j ACCEPT
done
netfilter-persistent save >/dev/null

log "SSH hardening: keys only"
cat > /etc/ssh/sshd_config.d/10-audit.conf <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
EOF
sshd -t && systemctl reload ssh

cat <<EOF

Done. Remaining manual steps:
  1. Add the GitHub Actions deploy public key to /home/deploy/.ssh/authorized_keys
  2. Enter R2 and Brevo values yourself:  sudo audit-set-secrets
  3. Open TCP 80/443 in the OCI security list / NSG for this VM.
  4. Run the first deploy from GitHub Actions (workflow "Deploy master").
EOF
