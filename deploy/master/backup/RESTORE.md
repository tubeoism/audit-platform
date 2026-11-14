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

## Kho bằng chứng (`/srv/audit/evidence`)

Bằng chứng không nằm trong PostgreSQL nên WAL-G không sao lưu chúng. Chúng có bản sao riêng:

| Mục | Giá trị |
|---|---|
| Công cụ | rclone v1.76.0 (`/usr/local/bin/rclone`), thiết lập bằng `sudo bash evidence/evidence-setup.sh [--enable]` |
| Nơi lưu | `s3://audit-live/evidence/` (mỗi file một đối tượng cùng tên với mã băm, thêm đuôi `.bin`) |
| Mã hoá | rclone crypt phía client, khoá ở `/etc/audit-evidence/crypt.key` (root:audit 0640), **khác khoá backup PostgreSQL**. Tên file không mã hoá (chỉ là mã băm), nội dung thì có. R2 chỉ thấy dữ liệu đã mã hoá |
| Chép | `audit-evidence-mirror.timer` mỗi 10 phút, chỉ `rclone copy`: không bao giờ xoá trên R2 vì file biến mất ở VM. `--immutable`: file cùng tên mà khác kích thước là lỗi chứ không ghi đè |
| Cảnh báo | `audit-evidence-mirror-check` mỗi giờ: báo khi lần chép cuối quá 2 giờ, hoặc có file trên VM (cũ hơn 30 phút) mà R2 thiếu hay khác kích thước |
| Diễn tập | `audit-evidence-restore-drill` ngày 02 hằng tháng: tải 20 file ngẫu nhiên từ R2, giải mã, so từng byte với bản trên VM, gửi PASS/FAIL qua mail |
| Kiểm tra toàn vẹn | `sudo audit-admin evidence verify` băm lại mọi file mà CSDL liệt kê, báo file THIẾU hoặc BỊ SỬA, ghi audit log (`evidence.verified`) |

> **Khoá bằng chứng là điểm sống còn thứ hai.** Chạy `sudo audit-evidence-key --show` một lần, cất cạnh khoá backup, ghi fingerprint (`--fingerprint`). Mất khoá thì bản sao trên R2 không đọc được.

Giới hạn cần biết: bản sao này chạy mỗi 10 phút, nên file vừa tải lên có thể chưa có trên R2 trong tối đa khoảng 10 phút; và R2 `audit-live` chưa có object lock, nên token R2 bị lộ có thể xoá bản sao. Việc đóng gói engagement đã kết thúc sang `audit-archive` (bất biến 10 năm) chưa làm.

### Kịch bản D: mất hoặc hỏng file bằng chứng trên VM

1. Xác định phạm vi: `sudo audit-admin evidence verify` (liệt kê THIẾU và BỊ SỬA).
2. Khôi phục từ R2 chỉ những file đó (hoặc cả kho nếu mất đĩa). `audit-evidence-restore-drill` và `evidence-mirror` dùng cùng cấu hình; trên VM đang chạy:
   ```bash
   H=<mã băm 64 ký tự>
   sudo install -d -o audit -g audit -m 0750 /srv/audit/evidence/${H:0:2}/${H:2:2}
   sudo -u audit rclone copyto evidence:${H:0:2}/${H:2:2}/$H /srv/audit/evidence/${H:0:2}/${H:2:2}/$H --config /etc/audit-evidence/rclone.conf
   sudo chmod 0444 /srv/audit/evidence/${H:0:2}/${H:2:2}/$H
   ```
   File bị sửa thì xoá bản hỏng trước (`sudo chattr -i`/`chmod u+w` nếu cần). Cả kho: `sudo -u audit rclone copy evidence: /srv/audit/evidence --config /etc/audit-evidence/rclone.conf --transfers 4`.
3. Chạy lại `sudo audit-admin evidence verify` và mong thấy `OK`. File nào vẫn THIẾU thì không còn bản sao nào: ghi biên bản, báo người phụ trách engagement để lấy lại bản gốc từ khách hàng.
4. Dựng lại VM từ đầu: sau kịch bản B, `sudo audit-evidence-key --install` (khoá ngoại tuyến), `sudo bash evidence/evidence-setup.sh` (không `--enable`), khôi phục cả kho như trên, `verify`, rồi mới `--enable`.

### Cảnh báo của bản sao bằng chứng

| Cảnh báo | Kiểm tra | Xử lý thường gặp |
|---|---|---|
| Lần chép cuối quá 2 giờ | `journalctl -u audit-evidence-mirror -n 100` | R2 token hết hạn: `sudo audit-set-secrets` rồi `sudo audit-evidence-config`. Mạng lỗi thì đợi |
| File trên VM mà R2 thiếu hoặc khác kích thước | `sudo audit-evidence-mirror-check` | Chạy `sudo systemctl start audit-evidence-mirror`. Nếu lỗi `immutable`/khác kích thước: một phía đã hỏng, chạy `audit-admin evidence verify` để biết phía nào, rồi khôi phục theo kịch bản D |
| Diễn tập FAIL | Mail báo cáo, `journalctl -u audit-evidence-restore-drill` | Coi như sự cố: chưa chứng minh được khả năng khôi phục bằng chứng. Xử lý trong ngày |

## Gói lưu trữ engagement (`audit-archive`)

Engagement đã khoá được đóng gói và lưu bất biến 10 năm bằng `sudo audit-engagement-archive <mã>` (xem `docs/engagement-archive.md`). Gói là một file tar mã hoá bằng **khoá bằng chứng** (cùng khoá với bản sao bằng chứng; mất khoá thì không đọc được gói).

Đọc lại một gói (trên VM đã cài `evidence-setup.sh`, hoặc máy khác có rclone và khoá qua `audit-evidence-key --install`). Làm trong một thư mục tạm mới và chạy mọi bước bằng cùng một người dùng, để không lẫn với gói khác:

```bash
D=$(sudo -u audit mktemp -d /srv/audit/archive-staging/read.XXXXXX)
sudo -u audit rclone lsf archive:<mã> --config /etc/audit-evidence/rclone.conf          # <mã> = tên engagement đã đổi ký tự lạ thành _
sudo -u audit rclone copyto archive:<mã>/<tên>.tar "$D/p.tar" --config /etc/audit-evidence/rclone.conf
sudo -u audit sh -c "cd '$D' && tar -xf p.tar && sha256sum -c engagement-*/SHA256SUMS"
```

`sha256sum -c` phải báo OK cho mọi file. Mã băm của cả file tar nằm trong audit log (`engagement.archived`, `tarSha256`). Đối tượng trong `audit-archive` không xoá được và không ghi đè được: gói lỗi hoặc dư vẫn nằm đó; chỉ cần không đánh dấu `Archived` cho nó.

## Thay khoá hoặc token

- **R2 token mới**: `sudo audit-set-secrets` (lệnh này gọi `audit-walg-config`), rồi `sudo audit-evidence-config` cho bản sao bằng chứng. Kiểm lại bằng `sudo audit-pg-backup-check` và `sudo audit-evidence-mirror-check`.
- **Khoá mã hoá**: không thay tại chỗ, vì bản cũ chỉ đọc được bằng khoá cũ. Nếu bắt buộc phải thay, đổi sang prefix mới (`LIVE_PREFIX=... audit-walg-config`), chụp bản full mới và giữ khoá cũ cho tới khi bản cũ hết hạn giữ (30 ngày, còn với archive là theo lock).

## Nhật ký diễn tập

| Ngày | Loại | Kết quả | Thời gian khôi phục | Ghi chú |
|---|---|---|---|---|
| 2026-11-13 | Cluster thử trên VM, prefix `pg-test/` | PASS | 14 s | Kiểm thêm: sai khoá thì bị từ chối, PITR đúng thời điểm, bản copy archive tự khôi phục được, đẩy WAL lỗi thì có cảnh báo |
| 2026-11-13 | DB thật, sau khi bật archiving | PASS | 13 s | DB mới chỉ có lịch sử migration, chưa có dữ liệu nghiệp vụ. Bản tháng đầu tiên `base_000000010000000000000002` đã chép sang `audit-archive` |
