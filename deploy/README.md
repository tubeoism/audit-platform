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
| `master/auth-key.sh` | Installed as `/usr/local/sbin/audit-auth-key`. Generates, shows (for the offline copy), fingerprints and re-installs `Auth__MasterKey`, the root of the authentication keys (ADR-0005) |
| `master/audit-admin.sh` | Installed as `/usr/local/sbin/audit-admin`. Account administration from the console as the `audit` user: `invite`, `list`, `disable`/`enable`, `role`, `reset-mfa`, `devices`, `revoke`; engagements and the methodology library: `engagement create|member|unmember|list`, `templates import|list` (see the root README). Each action is in the audit log with the sudo user's name |
| `master/backup/` | WAL-G continuous archiving and nightly base backups of PostgreSQL to R2 `audit-live`, hourly health check with e-mail alerts, monthly immutable copy to `audit-archive`, monthly restore drill. Setup: `sudo bash backup/backup-setup.sh [--enable]`. Runbook: [`master/backup/RESTORE.md`](master/backup/RESTORE.md) |
| `master/evidence/` | Encrypted off-site mirror of `/srv/audit/evidence` to R2 `audit-live/evidence` with rclone crypt: copy every 10 minutes (never deletes), hourly completeness check with e-mail alerts, monthly restore drill. Setup: `sudo bash evidence/evidence-setup.sh [--enable]`. Integrity of the store: `sudo audit-admin evidence verify`. Runbook: the evidence section of [`master/backup/RESTORE.md`](master/backup/RESTORE.md) |
| `master/r2-lock-test.sh` | Installed as `/usr/local/sbin/audit-r2-lock-test`. Writes a marker under `audit-archive/archive/` and checks that deleting it returns HTTP 409 |

## Layout on the VM

```
/opt/audit-master/releases/<sha>-<attempt>/{app/,efbundle}   last 5 kept
/opt/audit-master/current -> releases/<live>
/opt/audit-master/incoming/                                    uploads from CI (deploy user)
/etc/audit-master/master.env    service secrets (connection string as audit_app, Auth__MasterKey, R2, Brevo)
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
7. Backups: `sudo bash backup/backup-setup.sh --enable` (restarts PostgreSQL once), store the key offline with `sudo audit-walg-key --show`, then run `sudo audit-pg-restore-drill` and expect `RESULT: PASS`.

7a. Evidence mirror: `sudo bash evidence/evidence-setup.sh`, store the key offline with `sudo audit-evidence-key --show` (next to the backup key), then `sudo bash evidence/evidence-setup.sh --enable`. Needs the alert mail from step 7.

8. Authentication: `sudo audit-auth-key --init`, then `sudo audit-auth-key --show` and store the key offline next to the backup key. Invite the first administrator with `sudo audit-admin invite <email> "<name>" --admin`; they finish in the browser from the e-mailed link. Invite a second administrator the same way.

### Adding authentication to a VM bootstrapped before ADR-0005

```bash
# from a checkout of deploy/master on the VM
sudo install -o root -g root -m 0755 auth-key.sh /usr/local/sbin/audit-auth-key
sudo install -o root -g root -m 0755 audit-admin.sh /usr/local/sbin/audit-admin
sudo audit-auth-key --init        # generates the key and restarts the master
sudo audit-auth-key --show        # store offline, note the fingerprint
sudo audit-admin invite <email> "<name>" --admin
```

Until `--init` has run, the new release starts normally but answers 503 on every authentication endpoint.

## Day to day

- Accounts: `sudo audit-admin list`; a lost phone with no recovery codes left: `sudo audit-admin reset-mfa <email>`; a lost laptop: `sudo audit-admin devices <email>` then `sudo audit-admin revoke <email> <installation-id>`; a leaver: `sudo audit-admin disable <email>`. If mail delivery fails, add `--show-link` and pass the link to the user over a trusted channel.

- Merge to `main`; when CI passes, approve the deploy in Actions.
- Helper scripts: CD ships only the application. The scripts in `/usr/local/sbin` come from `bootstrap-vm.sh`, `backup/backup-setup.sh` and `evidence/evidence-setup.sh`, so after a PR that changes one of them, re-run the matching setup script on the VM (they are idempotent). The last step of every deploy runs `master/check-vm-drift.sh` (read-only, sha256) and shows a warning in the run summary for any script that is missing or differs. It was not made to install scripts itself, because that would let the `deploy` key put code on the VM that runs as root.
- Roll back: `ssh deploy@<vm> sudo /usr/local/sbin/audit-activate-release --rollback`
- Logs: `journalctl -u audit-master -f`, `/var/log/caddy/audit-master.log`

- Evidence mirror: `sudo audit-evidence-mirror-check`; integrity of the store: `sudo audit-admin evidence verify`.
- Backups: `sudo audit-pg-backup-check`; restore procedures in [`master/backup/RESTORE.md`](master/backup/RESTORE.md).
