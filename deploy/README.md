# Deployment (placeholder)

Deployment to the Oracle Cloud Arm VM is the next phase. This folder holds only the runtime contract so it can be reviewed with the code:

| File | Purpose |
|---|---|
| `master/audit-master.service` | systemd unit: runs the master as an unprivileged `audit` user, bound to 127.0.0.1:5080 behind the TLS reverse proxy for `app.audit.io.vn` |
| `master/master.env.example` | Names of the environment variables the master reads. Real values are entered on the VM only |

Planned for the deploy phase: VM hardening baseline, PostgreSQL 16 with separate owner (migrations) and application roles, reverse proxy with TLS, WAL-G continuous archiving and nightly base backups to R2 bucket `audit-live`, restore drill.

**Database roles.** Run migrations as the schema owner and the service as a non-owner role (`audit_app`) with `INSERT, SELECT` only on `audit_log`. The append-only trigger blocks UPDATE/DELETE/TRUNCATE for everyone, but only a non-owner role cannot drop the trigger.
