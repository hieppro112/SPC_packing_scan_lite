# Scale Scan Lite

Bản **không camera** của Scale Scan Pro: mã PO đến từ **máy quét barcode USB** (hoặc gõ tay), sau đó quy trình giữ nguyên:
tra PO từ SQL Server → đọc số kg từ cân điện tử qua cổng COM → so khối lượng chuẩn → lưu lịch sử → cảnh báo nếu lệch.

So với bản Pro: bỏ hoàn toàn camera, YOLO, OpenCV, ONNX Runtime và ZXing, nên gói nhỏ hơn nhiều, không cần Visual C++ Redistributable
và không còn lỗi thiếu `onnxruntime.dll`/`best.onnx` khi cài.

## Chạy thử ngay (không cần máy quét, cân hay SQL)

```powershell
dotnet run --project src/ScaleScanLite -- --demo
```

Chế độ `--demo` dùng cân giả, SQL giả trong bộ nhớ và tự "quét" lần lượt vài mã mẫu mỗi 15 giây.
Tự kiểm tra không người trông (mã thoát 0 = đạt): `ScaleScanLite.exe --selftest=35`

## Cách nhập mã

1. **Ô "Quét hoặc nhập mã PO"** (luôn được đặt sẵn con trỏ): quét bằng máy quét USB (máy quét gõ mã + Enter) hoặc gõ tay rồi Enter.
2. **Nghe nền bằng máy quét USB** (⚙ Cài đặt → *Máy quét barcode USB* → Nhận diện máy quét → quét thử → Lưu): quét khi đang dùng app khác
   hoặc khi Scale Scan Lite thu nhỏ; app kia vẫn nhận mã như bình thường, Scale Scan Lite nhận thêm một bản sao. Chỉ nghe đúng máy quét đã chọn
   (nhận theo VID/PID), bàn phím thường bị bỏ qua hoàn toàn. Chỉ dùng được với máy quét ở chế độ bàn phím (HID).

Quét lại đúng mã vừa cân xong = cân lại PO đó. Cùng một mã đến từ hai đường (ô nhập đang được chọn + nghe nền) trong 1,5 giây chỉ tính một lần.

## Luồng nghiệp vụ

1. Mã hợp lệ: chuỗi có dấu phẩy và hơn 2 phần → PO là phần tử thứ 2; hoặc chuỗi từ 11 ký tự → cả chuỗi là PO.
2. Tra PO trong SQL → bắt đầu hỏi cân (lệnh `SP` mỗi 500 ms); 60 giây không có số ổn định thì bỏ phiên.
3. Số cân ổn định (mặc định 5 lần đọc liên tiếp, lệch ≤ 0.005, ≥ 0.01) → lưu **một lần**; nút *Xác nhận cân* lưu ngay số hiện tại.
4. **PO quét sau cùng được ưu tiên**: quét nhầm PO1 rồi quét PO2 trước khi bỏ lên cân thì số cân lưu cho PO2 (PO1 bị bỏ, kết quả tra cứu muộn của PO1 bị hủy). Mã sai định dạng không làm mất PO đang chờ cân.
   Cân xong thì **xóa PO hiện tại**, chờ quét PO mới (số cân đọc thêm sau đó không lưu cho PO nào).
   PO đã có trong lịch sử mà cân lại: **cập nhật** số kg và thời gian của dòng đó (không tạo dòng mới) và hiện thông báo *cập nhật cân mới* kèm số kg cũ.
5. Khối lượng chuẩn = `round(NW/Qty, 3) × số lượng`; lệch quá `max(dung sai kg, dung sai %)` thì cảnh báo và ghi file JSON log.
6. Mất kết nối SQL: lưu tạm trên máy (`pending.jsonl`) và tự đồng bộ mỗi 30 giây.
7. Thu nhỏ xuống góc màn hình: cửa sổ nhỏ luôn nổi hiện số kg, mã PO, trạng thái và thông báo; hộp thông báo thường tự đóng sau 3 giây
   (không giành con trỏ của app đang dùng) nhưng vẫn có nút Xác nhận.

## Cấu trúc

| Project | Vai trò |
|---|---|
| `ScaleScanLite.Core` | Nghiệp vụ thuần: phân tích mã PO, khối lượng chuẩn + dung sai, số cân ổn định, ghép mã từ máy quét, phân trang, **workflow cân** |
| `ScaleScanLite` | Ứng dụng WPF (MVVM): giao diện, SQL, cổng COM, nghe máy quét (Raw Input), hàng đợi lưu tạm, Excel, cài đặt, log |
| `ScaleScanLite.Tests` | Test (xUnit) |

## Build, test, đóng gói

```powershell
dotnet build
dotnet test
.\publish.ps1            # self-contained, nén artifacts\ScaleScanLite-win-x64.zip
```

Dữ liệu (cấu hình, log, hàng đợi lưu tạm) nằm ở `%LocalAppData%\ScaleScanLite`, **tách riêng bản Pro** nên hai app không đè cấu hình của nhau
và có thể chạy cùng lúc trên một máy (lưu ý chỉ một app mở được cổng COM của cân). Đặt biến môi trường `SCALESCANLITE_DATA` để dùng thư mục khác.
Tài khoản SQL để trống nghĩa là dùng đăng nhập Windows; mật khẩu nhập ở màn hình Cài đặt (mã hóa DPAPI). Bản Pro dùng tài khoản `kuser` để tra PO
và `kproduct` cho lịch sử; nhập lại hai cặp này ở bản Lite.

## Chẩn đoán

Log: `%LocalAppData%\ScaleScanLite\logs\app.log` (xoay vòng). Chỉ cho phép một bản Lite chạy mỗi máy.