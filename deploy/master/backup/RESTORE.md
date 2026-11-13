# Sao lưu và khôi phục PostgreSQL của master (WAL-G → Cloudflare R2)

## Tóm tắt

| Mục | Giá trị |
|---|---|
| Công cụ | WAL-G v3.0.9 (`/usr/local/bin/wal-g`, gọi qua `audit-walg`) |
| Nơi lưu | `s3://audit-live/pg/audit-master`, mỗi tháng một bản bất biến ở `s3://audit-archive/archive/pg/audit-master` |
| WAL | Đẩy liên tục (`archive_command`), `archive_timeout = 300`, nên **RPO ≤ 5 phút** |
| Base backup | Full hằng ngày lúc 02:00 giờ VN (`audit-pg-backup.timer`) |
| Giữ bản | 30 bản full cùng WAL đi kèm, tức là khôi phục được về mọi thời điểm trong khoảng 30 ngày |
| Bản bất biến | Ngày 01 hằng tháng chép bản full mới nhất cùng WAL của nó sang `audit-archive/archive/` (object lock 10 năm, nên mỗi bản tháng tồn tại 10 năm và không xoá được) |
| Mã hoá | libsodium phía client, khoá ở `/etc/wal-g/libsodium.key` (root:postgres 0640). R2 chỉ thấy dữ liệu đã mã hoá |
| Nén | zstd |
| Cảnh báo | `audit-pg-backup-check` chạy mỗi giờ. Backup hoặc diễn tập lỗi thì `OnFailure=` gửi mail qua Brevo tới `BACKUP_ALERT_EMAIL` |
| Diễn tập | `audit-pg-restore-drill` chạy ngày 02 hằng tháng lúc 03:00 giờ VN và gửi báo cáo PASS/FAIL qua mail |
| RTO mục tiêu | ≤ 30 phút để khôi phục DB trên VM đang chạy. ≤ 4 giờ nếu phải dựng lại VM từ đầu |

Bản sao theo 3-2-1: (1) DB đang chạy trên đĩa VM, (2) `audit-live` trên R2, (3) bản bất biến hằng tháng ở `audit-archive`, cộng thêm **khoá giải mã giữ ngoại tuyến**.

> **Khoá giải mã là điểm sống còn.** Không có khoá thì không đọc được bất kỳ bản backup nào, kể cả bản trong archive. Chạy `sudo audit-walg-key --show` một lần, cất vào trình quản lý mật khẩu và một bản in. Ghi lại fingerprint (`sudo audit-walg-key --fingerprint`) để sau này đối chiếu.

## Lệnh hằng ngày

```bash
sudo audit-pg-backup-check                                # tình trạng: archiving, tuổi bản full mới nhất
sudo -u postgres audit-walg backup-list --detail --pretty # danh sách bản full
sudo -u postgres psql -c 'select * from pg_stat_archiver' # bộ đếm đẩy WAL
systemctl list-timers 'audit-*'
journalctl -u audit-pg-backup -u audit-pg-restore-drill -n 100
sudo systemctl start audit-pg-backup                      # chạy backup ngay
sudo audit-pg-restore-drill                               # diễn tập ngay (khoảng 1 phút khi DB còn nhỏ)
```

## Kịch bản A: khôi phục về một thời điểm trên chính VM (lỡ tay xoá hoặc sửa dữ liệu, migration hỏng)

1. Xác định thời điểm *trước* sự cố, có kèm múi giờ, ví dụ `2026-11-20 08:15:00+07`.
2. Dựng một bản sao tại thời điểm đó ở cổng 5433, không đụng DB thật:
   ```bash
   sudo audit-pg-restore-drill --target-time '2026-11-20 08:15:00+07'
   sudo -u postgres psql -h /var/lib/postgresql/restore-drill -p 5433 -d audit_master
   ```
   Instance tạm dừng ở thời điểm đích và không đẩy WAL (`archive_mode=off`). Nếu thời điểm chưa đúng, chạy `--cleanup` rồi thử lại với thời điểm khác.
3. Chọn một trong hai cách:
   - **Chỉ lấy lại một phần dữ liệu** (nên làm): xuất các dòng cần bằng `pg_dump -t` hoặc `\copy` từ cổng 5433 rồi nạp vào DB thật bằng `audit_owner`. `audit_log` là append-only nên không bao giờ "sửa" nó, chỉ bổ sung sự kiện bù trừ qua ứng dụng.
   - **Thay toàn bộ DB thật bằng bản tại thời điểm đó**: làm theo kịch bản B từ bước 4, thêm `recovery_target_time` và `recovery_target_action = 'promote'`. Toàn bộ thay đổi sau thời điểm đó sẽ mất, nên phải ghi biên bản.
4. Dọn dẹp: `sudo audit-pg-restore-drill --cleanup`.

## Kịch bản B: mất VM hoặc mất đĩa, dựng lại từ R2

Cần có: khoá giải mã (bản ngoại tuyến), quyền vào Cloudflare (R2 token) và GitHub (repo, environment `production`).

1. Tạo VM Ubuntu 24.04 aarch64 mới, chép `deploy/master/` lên và chạy `sudo bash bootstrap-vm.sh`.
2. `sudo audit-set-secrets`: nhập R2, Brevo, e-mail cảnh báo.
3. Cài khoá từ bản ngoại tuyến: `sudo audit-walg-key --install`, rồi đối chiếu `--fingerprint` với fingerprint đã ghi. Cài lại khoá xác thực theo cùng cách: `sudo audit-auth-key --install`. Nếu không còn khoá xác thực thì chạy `sudo audit-auth-key --init`; dữ liệu không mất nhưng mọi người dùng phải cài lại TOTP (`sudo audit-admin reset-mfa <email>`).
4. Cài WAL-G nhưng **chưa bật archiving**: `sudo bash backup/backup-setup.sh`. Không chạy `--enable` ở bước này, vì cluster mới mà đẩy WAL lên cùng prefix sẽ làm rối lịch sử.
5. Thay data directory bằng bản trên R2:
   ```bash
   sudo systemctl stop audit-master postgresql@16-main
   sudo mv /var/lib/postgresql/16/main /var/lib/postgresql/16/main.empty
   sudo install -d -o postgres -g postgres -m 0700 /var/lib/postgresql/16/main
   sudo -u postgres audit-walg backup-fetch /var/lib/postgresql/16/main LATEST
   sudo tee /etc/postgresql/16/main/conf.d/60-restore.conf <<'EOF'
   restore_command = '/usr/local/bin/audit-walg wal-fetch %f %p'
   recovery_target_timeline = 'latest'
   # recovery_target_time = '...'      # chỉ khi khôi phục về một thời điểm (kịch bản A)
   # recovery_target_action = 'promote'
   EOF
   sudo -u postgres touch /var/lib/postgresql/16/main/recovery.signal
   sudo systemctl start postgresql@16-main
   sudo -u postgres psql -XAtc 'select pg_is_in_recovery()'   # đợi tới khi ra 'f'
   ```
   Nếu R2 `audit-live` cũng mất, dùng bản tháng ở archive: thêm `WALG_CONFIG=/etc/wal-g/walg-archive.json` trước `audit-walg` ở cả `backup-fetch` lẫn `restore_command`. Bản tháng chỉ khôi phục được tới thời điểm chụp bản đó.
6. Kiểm tra: số dòng các bảng, `audit_log` (chuỗi hash, xem câu SQL trong `pg-restore-drill.sh`), và `select tgname from pg_trigger where not tgisinternal`.
7. Mật khẩu role trong DB đã khôi phục là mật khẩu của VM cũ. Đặt lại cho khớp `/etc/audit-master/*.env` của VM mới:
   ```bash
   sudo -u postgres psql -c "alter role audit_owner password '<từ migrate.env>'" -c "alter role audit_app password '<từ master.env>'"
   ```
8. Gỡ `60-restore.conf`. Bật archiving bằng `sudo bash backup/backup-setup.sh --enable`: lệnh này restart PostgreSQL, chụp ngay một bản full trên timeline mới và bật các timer.
9. `sudo systemctl start audit-master`, kiểm `curl -fsS http://127.0.0.1:5080/healthz/ready`, trỏ DNS hoặc IP mới (Cloudflare) và cập nhật `DEPLOY_HOST` và `DEPLOY_KNOWN_HOSTS` trong GitHub environment `production`.
10. Chạy `sudo audit-pg-restore-drill` để xác nhận chuỗi backup mới hoạt động.

## Kịch bản C: cảnh báo backup

| Cảnh báo | Kiểm tra | Xử lý thường gặp |
|---|---|---|
| Đẩy WAL thất bại | `journalctl -u postgresql@16-main -n 100`, `select * from pg_stat_archiver` | R2 token hết hạn hoặc bị đổi: `sudo audit-set-secrets` (tự cập nhật cấu hình WAL-G). Mạng tới R2 lỗi thì đợi, PostgreSQL tự thử lại |
| WAL chờ quá 15 phút | `ls /var/lib/postgresql/16/main/pg_wal/archive_status/*.ready \| wc -l` | Như trên. WAL ứ lại làm đầy đĩa, nên theo dõi `df -h /` |
| Bản full quá 26 giờ | `journalctl -u audit-pg-backup -n 100` | Chạy lại `sudo systemctl start audit-pg-backup` |
| Diễn tập FAIL | Mail báo cáo và `journalctl -u audit-pg-restore-drill` | Coi như sự cố: chưa chứng minh được khả năng khôi phục. Xử lý trong ngày |

## Thay khoá hoặc token

- **R2 token mới**: `sudo audit-set-secrets` (lệnh này gọi `audit-walg-config`). Kiểm lại bằng `sudo audit-pg-backup-check`.
- **Khoá mã hoá**: không thay tại chỗ, vì bản cũ chỉ đọc được bằng khoá cũ. Nếu bắt buộc phải thay, đổi sang prefix mới (`LIVE_PREFIX=... audit-walg-config`), chụp bản full mới và giữ khoá cũ cho tới khi bản cũ hết hạn giữ (30 ngày, còn với archive là theo lock).

## Nhật ký diễn tập

| Ngày | Loại | Kết quả | Thời gian khôi phục | Ghi chú |
|---|---|---|---|---|
| 2026-11-13 | Cluster thử trên VM, prefix `pg-test/` | PASS | 14 s | Kiểm thêm: sai khoá thì bị từ chối, PITR đúng thời điểm, bản copy archive tự khôi phục được, đẩy WAL lỗi thì có cảnh báo |
| 2026-11-13 | DB thật, sau khi bật archiving | PASS | 13 s | DB mới chỉ có lịch sử migration, chưa có dữ liệu nghiệp vụ. Bản tháng đầu tiên `base_000000010000000000000002` đã chép sang `audit-archive` |
