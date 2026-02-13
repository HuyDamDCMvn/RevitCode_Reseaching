# Phân tích Rules & Patterns trong Repo – Dùng được không?

## Tóm tắt

| Loại | Đường dẫn | Đang dùng trong code? | Dùng cho ML được không? |
|------|-----------|------------------------|-------------------------|
| **Rules (Tagging)** | `Data/Rules/Tagging/*.json` | **Có** – RuleEngine + TagPlacementService | **Có** – Ưu tiên vị trí, offset, scoring |
| **Rules (Dimension)** | `Data/Rules/Dimension/*.json` | **Có** – RuleEngine | Không dùng cho tag placement |
| **Patterns (TagPositions)** | `Data/Patterns/TagPositions/*.json` | **Có** – TagPositionPatternLoader | **Có** – position text → TagPosition, dùng khi không có Rule |

---

## 1. Rules – Đang dùng và dùng được

### 1.1 Nơi load và dùng

- **RuleEngine** (`Services/RuleEngine.cs`) load tất cả file trong `Data/Rules/Tagging/*.json` khi `Initialize()`.
- **TagPlacementService** dùng Rules cho:
  - `GetPreferredPositionsFromRule(element)` → danh sách vị trí ưu tiên (TopCenter, BottomCenter, …).
  - `GetRuleSettings(element)` → offset, addLeader, collisionPenalty, preferenceBonus, alignmentBonus, avoidCollisionWith, groupAlignment.

### 1.2 Nội dung Rules có gì (liên quan placement)

Trong mỗi file rule (ví dụ `mep_pipes_ducts.json`, `sanitary_drainage.json`):

```json
"actions": {
  "preferredPositions": ["TopCenter", "BottomCenter", "Center"],
  "offsetDistance": 0.2,
  "addLeader": false,
  "avoidCollisionWith": ["OST_DuctCurves", "OST_PipeCurves"],
  "groupAlignment": "AlongCenterline"
},
"scoring": {
  "collisionPenalty": -100,
  "preferenceBonus": 40,
  "alignmentBonus": 20,
  "nearEdgeBonus": 5
}
```

- **preferredPositions**: dùng được trực tiếp cho placement (và đã dùng trong TagPlacementService).
- **offsetDistance, addLeader, avoidCollisionWith, groupAlignment**: dùng được cho logic đặt tag và CSP.
- **scoring**: dùng được cho điểm hóa candidate (đã dùng trong TagPlacementService).

**Kết luận:** Rules **dùng được** và **đã đang dùng** trong placement. ML pipeline (PlacementEngine) nên **gọi thêm RuleEngine** để:
- Sắp xếp/thêm candidate theo `preferredPositions`,
- Áp dụng `offsetDistance` / `addLeader` / `avoidCollisionWith` khi tạo và chấm điểm candidate.

---

## 2. Patterns (TagPositions) – Chưa load, dùng được một phần

### 2.1 Cấu trúc hiện tại

- Nằm trong `Data/Patterns/TagPositions/` (ví dụ: `hvac_rlt_munichre.json`, `sanitary_san_munichre.json`).
- Mỗi file có:
  - **source**: project, drawings, discipline, scale.
  - **legend**: chú thích hệ thống (ZUL, ABL, SW, PWC-TWK, …).
  - **observations**: mảng mô tả cách tag:
    - `elementType`: "Duct Tag - Round", "Waste Pipe Tag - Horizontal", …
    - `labelingPattern` / `labelingPatterns`: format chữ (không phải vị trí).
    - `position`: **chuỗi mô tả** vị trí, ví dụ:
      - `"Along duct centerline"`
      - `"Above/Below pipe"`
    - `examples`: ví dụ text tag.

- **RuleEngine chỉ load Rules/Tagging và Rules/Dimension**, **không** load thư mục `Patterns/TagPositions`.

### 2.2 Loader đã triển khai (TagPositionPatternLoader)

- **Services/TagPositionPatternLoader.cs** load tất cả file trong `Data/Patterns/TagPositions/*.json` (trừ file bắt đầu bằng `_`).
- Mỗi observation có thể có:
  - **position** (text, ví dụ "Along duct centerline") → map sang enum TagPosition.
  - **tagPosition** (enum trực tiếp, ví dụ "Left", "Center").
  - **hasLeader** (bool) → dùng cho gợi ý leader.
- Match theo category + discipline (từ tên file) + scale (từ source).
- **TagPlacementService** và **PlacementEngine** đều gọi pattern loader khi không có rule trùng: ưu tiên vị trí theo pattern.

### 2.3 So sánh với Training schema (ML)

Training sample cần: **element** (category, orientation, size, …) + **context** (density, neighbors, …) + **tag** (position enum, offsetX, offsetY, hasLeader, …).

Patterns hiện có:
- Có: elementType (gần category), source (project, scale), position **dạng text**.
- Không có: offsetX/offsetY, context (density, neighbors), bounding box.

**Kết luận:**  
- **Không thể** dùng trực tiếp Patterns làm training sample đủ chuẩn (thiếu offset, context).  
- **Có thể dùng một phần** bằng cách:
  - Thêm loader đọc `Data/Patterns/TagPositions/*.json`.
  - Map chuỗi `position` → enum TagPosition, ví dụ:
    - "Along duct centerline", "Along centerline" → **Center**
    - "Above", "Above pipe" → **TopCenter** / **TopRight**
    - "Below" → **BottomCenter** / **BottomLeft**
  - Dùng làm **hint ưu tiên vị trí** theo discipline/scale (tương tự Rules), hoặc làm **pseudo-samples** cho KNN (với context/offset mặc định).

---

## 3. Khuyến nghị

### 3.1 Rules

- **Giữ nguyên** cách dùng hiện tại trong TagPlacementService.
- **Bổ sung**: cho **PlacementEngine** (ML pipeline) gọi RuleEngine để:
  - Lấy `preferredPositions` và `GetRuleSettings` khi tạo/sắp xếp candidate và khi chạy CSP.

Như vậy Rules vừa dùng được, vừa được tận dụng tối đa cho cả heuristic và ML.

### 3.2 Patterns (TagPositions)

- **Option A (nhanh):** Thêm loader đọc Patterns, map `observations[].position` (text) → TagPosition, và dùng làm nguồn “ưu tiên vị trí” theo project/discipline/scale (bổ sung cho Rules).
- **Option B (đầy đủ hơn):** Khi có export từ Revit (TrainingDataExporter), tạo thêm script convert Patterns → file training dạng “pseudo-samples” (position + context mặc định) để KNN có thêm dữ liệu, với ghi chú là confidence thấp.

---

## 4. File tham chiếu trong repo

| Mục đích | File |
|----------|------|
| Load Rules | `Services/RuleEngine.cs` (Tagging + Dimension) |
| Dùng Rules trong placement | `Services/TagPlacementService.cs` (GetPreferredPositionsFromRule, GetRuleSettings) |
| Schema Rules | `Data/Schema/TaggingRule.schema.json` |
| Ví dụ Rules | `Data/Rules/Tagging/mep_pipes_ducts.json`, `sanitary_drainage.json`, … |
| Patterns (chưa load) | `Data/Patterns/TagPositions/*.json`, `_template.json` |
| Template Pattern | `Data/Patterns/TagPositions/_template.json` |

---

**Kết luận chung:**  
- **Rules:** Đang dùng, dùng được đầy đủ cho placement và nên tích hợp thêm vào PlacementEngine.  
- **Patterns:** Chưa được load; dùng được một phần (position text → ưu tiên vị trí hoặc pseudo training) nếu thêm loader và mapping.
