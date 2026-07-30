# VibeCodeReg — Runbook trước vận hành

Tài liệu này không thay đổi cấu hình hay trạng thái runtime. Mục tiêu là giúp operator kiểm tra trước release và có quy trình phản ứng khi có sự cố.

## 1. Nguyên tắc trước khi release

- Không commit token, API key, webhook secret, hay dữ liệu khách hàng vào Git.
- Chỉ chạy dashboard trên loopback (`127.0.0.1`/`localhost`); không public trực tiếp port dashboard.
- Dùng một máy/instance production riêng, không chạy bản development đang có thay đổi chưa commit.
- Không cập nhật ứng dụng ngay trên instance đang xử lý payment. Chuẩn bị bản mới, kiểm tra, rồi chuyển đổi trong cửa sổ bảo trì.
- Chỉ chạy automation với hệ thống mà bạn có quyền sử dụng bằng văn bản.

## 2. Secret và cấu hình

Các secret hiện hữu trong Git phải được coi là đã lộ. Trước production hãy tự rotate tại từng nhà cung cấp, sau đó đưa secret mới ra khỏi repository.

Ứng dụng đã hỗ trợ environment variables dạng .NET, ví dụ:

```powershell
[Environment]::SetEnvironmentVariable('Telegram__BotToken', '<token-moi>', 'User')
[Environment]::SetEnvironmentVariable('SePay__WebhookSecret', '<secret-moi>', 'User')
```

Đóng và mở lại terminal/service sau khi đặt biến môi trường. Không dán giá trị secret vào ticket, log hoặc screenshot.

`appsettings.json` và `Configuration/settings.template.json` chỉ được chứa template trống. Đặt file config local có secret vào vị trí không được Git theo dõi hoặc dùng biến môi trường.

## 3. Preflight (chỉ đọc)

Từ thư mục repository, chạy:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Readiness.ps1
```

Script chỉ đọc file/config/Git metadata, không gọi payment provider, không đổi database, không restart tiến trình. Kết quả `FAIL` phải được giải quyết trước release; `WARN` cần được chấp nhận có chủ đích.

Sau đó chạy build:

```powershell
dotnet build .\BAOCODETOOL.csproj --no-restore -v:minimal
dotnet list .\BAOCODETOOL.csproj package --vulnerable --include-transitive
```

## 4. Payment và webhook

- Bảo đảm clock của host đồng bộ NTP; webhook chỉ chấp nhận cửa sổ thời gian giới hạn.
- Bảo quản `WebhookSecret` và `GatewayAccessToken` như credential cấp production.
- Theo dõi các transaction trạng thái `Unmatched`, `AmountMismatch`, `Expired`, `Ignored` hằng ngày.
- Không chỉnh số dư trực tiếp trong SQLite. Mọi điều chỉnh phải có lý do và có transaction/audit record.
- Khi có mismatch, dừng điều chỉnh thủ công hàng loạt, giữ lại payload/transaction ID đã redact và đối chiếu với bank/provider.

## 5. Backup và rollback

- Backup SQLite trước mỗi release và trước thay đổi schema. Với file SQLite, cần dừng ứng dụng hoặc dùng cơ chế online backup; không copy riêng file `.db` khi WAL còn hoạt động.
- Giữ tối thiểu: bản EXE trước đó, backup DB, file cấu hình không có secret trong repository, và ghi chú release.
- Rollback: dừng instance, sao lưu DB hiện tại, đưa bản EXE trước đó trở lại, chỉ restore DB khi xác nhận schema/data mới không còn cần thiết.

## 6. Checklist go-live

- [ ] Tất cả secret đã rotate ngoài nhà cung cấp và không còn trong Git.
- [ ] `Test-Readiness.ps1` không có `FAIL`.
- [ ] Build thành công và dependency vulnerability đã được xử lý/đánh giá.
- [ ] Dashboard chỉ loopback và có biện pháp kiểm soát truy cập trước khi public bất kỳ cổng nào.
- [ ] Đã test webhook bằng payload sandbox/được ký hợp lệ.
- [ ] Đã có backup DB nhất quán và quy trình rollback được thử trên bản copy.
- [ ] Có operator chịu trách nhiệm theo dõi job queue, lỗi payment và dung lượng đĩa.

