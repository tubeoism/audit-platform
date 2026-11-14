#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-evidence-key. Manages the key that encrypts the evidence mirror on R2.
#   sudo audit-evidence-key --fingerprint   short fingerprint, safe to note anywhere (compare the offline copy against it)
#   sudo audit-evidence-key --show          print the key ONCE so you can store an offline copy. Without it the mirror cannot be read.
#   sudo audit-evidence-key --install       paste a key from your offline copy (new VM / disaster recovery). Input is hidden.
# This key is separate from the database backup key (audit-walg-key): keep both offline.
set -euo pipefail
KEY=/etc/audit-evidence/crypt.key
[[ $EUID -eq 0 ]] || { echo "Run with sudo" >&2; exit 1; }
fp() { tr -d '[:space:]' < "$KEY" | sha256sum | cut -c1-16; }

case "${1:-}" in
  --fingerprint) fp ;;
  --show)
    echo "Khoá giải mã kho bằng chứng trên R2 (hex, 64 ký tự). Lưu ngoại tuyến cùng chỗ với khoá backup."
    echo "Mất khoá này = không đọc được bản sao bằng chứng trên R2."
    echo; cat "$KEY"; echo
    echo "Fingerprint: $(fp)"
    logger -t audit-evidence-key "evidence key displayed by ${SUDO_USER:-root}" ;;
  --install)
    [[ -s "$KEY" ]] && { read -rp "Đã có khoá (fingerprint $(fp)). Ghi đè? Gõ YES: " ok; [[ "$ok" == YES ]] || exit 1; }
    read -rsp "Dán khoá hex 64 ký tự: " k; echo
    [[ "$k" =~ ^[0-9a-fA-F]{64}$ ]] || { echo "Khoá không hợp lệ" >&2; exit 1; }
    install -d -o root -g audit -m 0750 /etc/audit-evidence
    umask 027; printf '%s\n' "$k" > "$KEY"; chown root:audit "$KEY"; chmod 0640 "$KEY"
    /usr/local/sbin/audit-evidence-config
    echo "Đã cài khoá, fingerprint $(fp)" ;;
  *) sed -n '2,6p' "$0"; exit 2 ;;
esac
