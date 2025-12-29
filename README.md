# 🎥 ScreenStreaming

**ScreenStreaming** là một project C# cho phép stream màn hình (client và server).  
Repository này chứa mã nguồn cho cả phần **client** và **server** streaming. :contentReference[oaicite:1]{index=1}

---

## 📌 Mục đích

Mục tiêu của dự án là tạo một hệ thống streaming màn hình đơn giản để:
- Gửi hình ảnh/màn hình từ client lên server
- Hiển thị/nhận dữ liệu stream trên server hoặc ứng dụng khác

(Dựa trên tên và cấu trúc project) :contentReference[oaicite:2]{index=2}

---

## 🧱 Cấu trúc thư mục

```text
ScreenStreaming/
├── ClientStream/          # Phần Client 
├── ServerStreaming/       # Phần server xử lý streaming
├── .gitignore



## Hướng dẫn sử dụng
1. Clone project
git clone https://github.com/YudGnas/ScreenStreaming.git

2. Mở project
Mở file ScreenStreaming.sln trong Visual Studio.

3. Chạy ứng dụng
- Server

Chọn ServerStreaming làm startup project.

Nhấn Run để khởi động server streaming.

- Client

Chọn ClientStream làm startup project.

Nhấn Run để khởi động client.

Client sẽ kết nối tới server để gửi stream.
