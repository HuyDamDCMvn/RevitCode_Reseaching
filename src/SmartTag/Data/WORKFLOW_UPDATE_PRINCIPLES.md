# Cập nhật nguyên lý (Rules/Patterns) từ bản vẽ của bạn

## Phân luồng ưu tiên

Tool luôn ưu tiên **rule và pattern nội bộ** trước, sau đó mới dùng dữ liệu từ export của người dùng (nếu có):

1. **Rule nội bộ** – `Data/Rules/Tagging/*.json` (ưu tiên cao nhất)
2. **Pattern nội bộ** – `Data/Patterns/TagPositions/*.json`
3. **Learned (user export)** – `Data/Training/learned_overrides.json` (chỉ dùng khi không có rule/pattern trùng, hoặc bù offset/leader khi rule không chỉ định)

Như vậy: nếu đã có rule/pattern cho category đó thì luôn dùng; chỉ khi không có hoặc thiếu tham số thì mới lấy từ learned (JSON export của người dùng).

---

## Tự học khi export (không cần nhắn AI)

Khi bạn nhấn **"Export training data from view"** trong Revit:

1. Tool export JSON vào `Data/Training/annotated/`.
2. **Tự động**: Tool đọc lại file vừa export, gom theo **category** (và **category+system**), tính:
   - vị trí tag hay gặp nhất → `preferredPositions`
   - có leader hay không (đa số) → `addLeader`
   - khoảng cách offset trung bình → `offsetDistance`
3. Kết quả ghi vào **`Data/Training/learned_overrides.json`** (không sửa file Rules/Patterns gốc).
4. Lần đặt tag sau: dùng **Rule** → **Pattern** trước; nếu không có hoặc thiếu thì mới dùng **Learned** từ file trên.

Bạn **không cần nhắn AI** để cập nhật: chỉ cần export từ view đã tag chuẩn khi muốn bổ sung kiểu đặt tag của mình; rule/pattern nội bộ vẫn được ưu tiên.

---

## Tôi có đọc được file .rvt không?

**Không.** Ở đây (Cursor/IDE) không có Revit chạy, và file `.rvt` là định dạng nhị phân của Revit, chỉ mở được bằng Revit hoặc Autodesk Forge API.  
Vì vậy **upload file .rvt vào repo không giúp tôi tự đọc và cập nhật nguyên lý được**.

## Cách làm đúng: Export trong Revit → Đưa JSON vào repo → Tôi cập nhật

### Bước 1: Trong Revit

1. Mở file **.rvt** của bạn trong Revit.
2. Mở một **view** đã được tag chuẩn (floor plan / ceiling plan / section có nhiều tag).
3. Mở **Smart Tag** (pyRevit → Smart Tag).
4. Nhấn **"Export training data from view"**.
5. Tool sẽ:
   - Thu thập mọi element **đã có tag** trong view
   - Ghi ra file JSON (element + context + vị trí tag thực tế)
   - Lưu vào thư mục **Data/Training/annotated/** (cạnh DLL hoặc đường dẫn dev)

### Bước 2: Đưa file JSON vào repo

- Nếu build từ repo và chạy từ `HD.extension`, file export có thể đã nằm trong:
  - `src/SmartTag/Data/Training/annotated/exported_<ViewName>_<date>.json`
- Copy file đó vào repo (hoặc đảm bảo nó nằm trong workspace).
- Commit và push nếu bạn muốn tôi đọc từ repo.

### Bước 3: Báo tôi cập nhật nguyên lý

Trong chat, bạn viết rõ:

- **"Cập nhật nguyên lý từ file exported_xxx.json"**  
  hoặc  
- Đính kèm / paste đường dẫn file JSON (ví dụ: `Data/Training/annotated/exported_FloorPlan_20260213_1430.json`).

Tôi sẽ:

- Đọc file JSON đã export,
- Phân tích theo category, vị trí tag (position), offset, hasLeader,
- Đề xuất (hoặc sinh) cập nhật:
  - **Rules** (`Data/Rules/Tagging/*.json`): `preferredPositions`, `offsetDistance`, `addLeader`, v.v.
  - **Patterns** (`Data/Patterns/TagPositions/*.json`): thêm/sửa `observations` với `position`, `hasLeader` phù hợp bản vẽ của bạn.

## Tóm tắt

| Bạn làm | Tôi làm được? |
|--------|----------------|
| Upload file **.rvt** | ❌ Không đọc được .rvt |
| Export trong Revit → file **.json** → đưa vào repo | ✅ Đọc JSON và cập nhật Rules/Patterns |

**Quy trình:** Mở .rvt trong Revit → View đã tag chuẩn → Smart Tag → **Export training data from view** → Đưa file JSON vào repo → Nhắn tôi **"cập nhật nguyên lý từ file &lt;tên file&gt;"**.
