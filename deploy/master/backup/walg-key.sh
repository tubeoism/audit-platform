#!/usr/bin/env bash
# Installed as /usr/local/sbin/audit-walg-key. Manages the libsodium key that encrypts every backup.
#   sudo audit-walg-key --fingerprint   short fingerprint, safe to note anywhere (compare offline copy against it)
#   sudo audit-walg-key --show          print the key ONCE so you can store an offline copy. Without it, no backup can be read.
#   sudo audit-walg-key --install       paste a key from your offline copy (new VM / disaster recovery). Input is hidden.
set -euo pipefail
KEY=/etc/wal-g/libsodium.key
[[ $EUID -eq 0 ]] || { echo "Run with sudo" >&2; exit 1; }
fp() { tr -d '[:space:]' < "$KEY" | sha256sum | cut -c1-16; }

case "${1:-}" in
  --fingerprint) fp ;;
  --show)
    echo "Khoá giải mã backup (hex, 64 ký tự). Lưu ngoại tuyến: trình quản lý mật khẩu + bản in cất an toàn."
    echo "Mất khoá này = không đọc được bất kỳ bản backup nào."
    echo; cat "$KEY"; echo
    echo "Fingerprint: $(fp)"
    logger -t audit-walg-key "backup key displayed by ${SUDO_USER:-root}" ;;
  --install)
    [[ -s "$KEY" ]] && { read -rp "Đã có khoá (fingerprint $(fp)). Ghi đè? Gõ YES: " ok; [[ "$ok" == YES ]] || exit 1; }
    read -rsp "Dán khoá hex 64 ký tự: " k; echo
    [[ "$k" =~ ^[0-9a-fA-F]{64}$ ]] || { echo "Khoá không hợp lệ" >&2; exit 1; }
    install -d -o root -g postgres -m 0750 /etc/wal-g
    umask 027; printf '%s\n' "$k" > "$KEY"; chown root:postgres "$KEY"; chmod 0640 "$KEY"
    echo "Đã cài khoá, fingerprint $(fp)" ;;
  *) sed -n '2,6p' "$0"; exit 2 ;;
esac
