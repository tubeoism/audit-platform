# Deployment: master on the Oracle Cloud Arm VM

The master runs on one Ubuntu 24.04 aarch64 VM behind Caddy (TLS) at `https://app.audit.io.vn`. Cloudflare DNS is proxied with SSL mode **Full (strict)**. Releases are shipped by the GitHub Actions workflow **Deploy master** after CI passes on `main`. The `production` environment accepts only `main`, holds the deploy key and requires a reviewer, so each deploy waits for approval in Actions.

| File | Purpose |
|---|---|
| `master/bootstrap-vm.sh` | One-time, idempotent VM setup: packages (.NET 10 runtime, PostgreSQL 16, Caddy), `audit` and `deploy` users, DB roles with generated passwords, systemd unit, Caddy, host firewall, SSH hardening |
| `master/activate-release.sh` | Installed as `/usr/local/sbin/audit-activate-release`. Unpacks a release, runs migrations as the schema owner, re-applies grants, switches `current`, restarts, health-checks, rolls back on failure. `--rollback` switches to the previous release |
| `master/audit-master.service` | systemd unit: unprivileged `audit` user on 127.0.0.1:5080 |
| `master/Caddyfile` | Reverse proxy with automatic Let's Encrypt certificate; WebSockets pass through for SignalR |
| `master/master.env.example` | Variable names the master reads. Real values exist only in `/etc/audit-master/master.env` (root, 0600) |
| `master/set-secrets.sh` | Installed as `/usr/local/sbin/audit-set-secrets`. Prompts for R2 and Brevo values (hidden input) and writes them into `master.env` |
| `master/r2-lock-test.sh` | Installed as `/usr/local/sbin/audit-r2-lock-test`. Writes a marker under `audit-archive/archive/` and checks that deleting it returns HTTP 409 |

## Layout on the VM

```
/opt/audit-master/releases/<sha>-<attempt>/{app/,efbundle}   last 5 kept
/opt/audit-master/current -> releases/<live>
/opt/audit-master/incoming/                                    uploads from CI (deploy user)
/etc/audit-master/master.env    service secrets (connection string as audit_app, R2, Brevo)
/etc/audit-master/migrate.env   schema-owner connection used only for migrations
/srv/audit/evidence             evidence store
```

**Database roles.** Migrations run as `audit_owner`; the service runs as `audit_app`, which has `INSERT, SELECT` only on `audit_log`. The append-only trigger blocks UPDATE/DELETE/TRUNCATE for everyone, and a non-owner role cannot drop the trigger.

**Deploy user.** `deploy` can write to `incoming/` and run exactly one command as root: `audit-activate-release`. Its SSH key is used only by GitHub Actions.

## First-time setup

1. Copy `deploy/master/` to the VM and run `sudo bash bootstrap-vm.sh`.
2. Open TCP 80 and 443 in the OCI security list or NSG.
3. Generate a deploy key pair, put the public key in `/home/deploy/.ssh/authorized_keys`.
4. In GitHub, create environment `production` limited to the `main` branch with a required reviewer, then add:
   - secret `DEPLOY_SSH_KEY`: the private deploy key
   - secret `DEPLOY_KNOWN_HOSTS`: output of `ssh-keyscan -t ed25519 <vm-ip>`
   - variable `DEPLOY_HOST`: the VM IP
5. Enter R2 and Brevo values yourself with `sudo audit-set-secrets` (prompts, input hidden). Never commit them.
6. Run **Deploy master** from the Actions tab, approve, then run `sudo bash r2-lock-test.sh` and expect `PASS`.

## Day to day

- Merge to `main`; when CI passes, approve the deploy in Actions.
- Roll back: `ssh deploy@<vm> sudo /usr/local/sbin/audit-activate-release --rollback`
- Logs: `journalctl -u audit-master -f`, `/var/log/caddy/audit-master.log`

Still to come: WAL-G continuous archiving and nightly base backups to `audit-live`, and a restore drill.
