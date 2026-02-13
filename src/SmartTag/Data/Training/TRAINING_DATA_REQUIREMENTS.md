# Training Data Requirements for SmartTag ML

## Overview

Để train hệ thống ML placement, cần thu thập dữ liệu từ các bản vẽ chuyên nghiệp đã được tag tốt.

---

## 1. Dữ liệu cần thu thập

### 1.1 Từ Revit Model (Export tự động)

| Loại dữ liệu | Mô tả | Format |
|--------------|-------|--------|
| **Element data** | Thông tin element được tag | JSON |
| **Tag data** | Vị trí tag thực tế | JSON |
| **View data** | Scale, crop box | JSON |
| **Context data** | Neighbors, density | JSON |

### 1.2 File cần export từ mỗi View

```
📁 training_export_<project>_<view>/
├── elements.json       # Tất cả elements trong view
├── tags.json           # Tất cả tags đã đặt
├── view_info.json      # View scale, crop, type
└── screenshot.png      # Screenshot để verify (optional)
```

---

## 2. Schema chi tiết cho Training Data

### 2.1 File chính: `annotated_data.json`

```json
{
  "version": "1.0",
  "source": {
    "project": "MunichRE Revitalization",
    "discipline": "Sanitary",
    "drawings": ["SAN_01_0001", "SAN_01_0002"],
    "viewScale": 50,
    "annotatedBy": "Tên người annotate",
    "annotatedDate": "2026-02-13"
  },
  "samples": [
    {
      "id": "sample_001",
      "element": { ... },
      "context": { ... },
      "tag": { ... }
    }
  ]
}
```

### 2.2 Element Data

```json
{
  "element": {
    "category": "OST_PipeCurves",        // BuiltInCategory
    "familyName": "Pipe Types",          // Family name
    "typeName": "Standard",              // Type name
    "orientation": 0,                    // Góc (degrees, 0-360)
    "isLinear": true,                    // Pipe/Duct/Conduit = true
    "length": 15.5,                      // Chiều dài (feet)
    "width": 0.33,                       // Chiều rộng (feet)
    "height": 0.33,                      // Chiều cao (feet)
    "diameter": 0.33,                    // Đường kính (feet, nếu tròn)
    "systemType": "Domestic Cold Water", // MEP System Type
    "centerX": 10.5,                     // Tâm X trong view coords
    "centerY": 25.3                      // Tâm Y trong view coords
  }
}
```

### 2.3 Context Data

```json
{
  "context": {
    "density": "medium",                 // low | medium | high
    "neighborCount": 3,                  // Số neighbors trong radius
    "hasNeighborAbove": true,            // Có neighbor phía trên?
    "hasNeighborBelow": false,
    "hasNeighborLeft": false,
    "hasNeighborRight": true,
    "distanceToNearestAbove": 2.5,       // Khoảng cách (feet)
    "distanceToNearestBelow": 10.0,
    "distanceToNearestLeft": 8.0,
    "distanceToNearestRight": 3.0,
    "distanceToWall": 5.0,               // Khoảng cách đến tường gần nhất
    "parallelElementsCount": 2,          // Số ống song song
    "isInGroup": false                   // Element có trong group?
  }
}
```

### 2.4 Tag Data (Ground Truth)

```json
{
  "tag": {
    "position": "BottomCenter",          // Vị trí tương đối với element
    "offsetX": 0,                        // Offset X từ center (feet)
    "offsetY": -1.5,                     // Offset Y từ center (feet)
    "hasLeader": false,                  // Có leader line?
    "leaderLength": 0,                   // Chiều dài leader (feet)
    "rotation": "Horizontal",            // Horizontal | Vertical
    "alignedWithRow": true,              // Có align với row ngang?
    "alignedWithColumn": false,          // Có align với column dọc?
    "rowId": "row_01",                   // ID của row (nếu aligned)
    "columnId": null,
    "tagText": "PWC-TWK DN100",          // Text hiển thị trong tag
    "tagWidth": 2.5,                     // Chiều rộng tag thực tế (feet)
    "tagHeight": 0.8                     // Chiều cao tag thực tế (feet)
  }
}
```

---

## 3. Các Project Mẫu Cần Thu Thập

### 3.1 Yêu cầu chung
- **Bản vẽ chuyên nghiệp** đã được tag theo tiêu chuẩn Đức/châu Âu
- **Các scale khác nhau**: 1:50, 1:100, 1:200
- **Các discipline khác nhau**: Sanitary, HVAC, Electrical, Plumbing

### 3.2 Target số lượng

| Discipline | Scale 1:50 | Scale 1:100 | Scale 1:200 | Total |
|------------|------------|-------------|-------------|-------|
| Sanitary (Pipes) | 150 | 100 | 50 | 300 |
| HVAC (Ducts) | 150 | 100 | 50 | 300 |
| Electrical | 100 | 100 | 50 | 250 |
| Cable Tray | 100 | 100 | 50 | 250 |
| Equipment | 50 | 50 | 50 | 150 |
| **Total** | **550** | **450** | **250** | **1250** |

---

## 4. Cách Thu Thập Dữ Liệu

### 4.1 Export từ Revit (Automatic)

Sẽ có tool export tự động trong SmartTag:
1. Mở view đã được tag tốt
2. Chạy "Export Training Data"
3. Tool tự động extract element + tag + context

### 4.2 Manual Annotation (nếu cần)

Nếu tag chưa có, cần annotate thủ công:
1. Export elements từ view
2. Dùng Annotation Tool để đánh dấu vị trí tag lý tưởng
3. Tool tính toán offset, context tự động

---

## 5. File Structure

```
📁 src/SmartTag/Data/Training/
├── 📁 annotated/                    # Training data đã annotate
│   ├── munichre_sanitary_50.json    # MunichRE, Sanitary, 1:50
│   ├── munichre_hvac_50.json
│   ├── arena_electrical_100.json
│   ├── arena_cabletray_100.json
│   └── ...
├── 📁 feedback/                     # User feedback (continuous learning)
│   ├── feedback_2026-02-13.json
│   └── ...
├── 📁 raw_exports/                  # Raw exports từ Revit (chưa annotate)
│   └── ...
└── TRAINING_DATA_REQUIREMENTS.md   # File này
```

---

## 6. Validation Checklist

Mỗi sample cần pass các check sau:

- [ ] `element.category` là valid BuiltInCategory
- [ ] `element.centerX/Y` trong view bounds
- [ ] `tag.position` là 1 trong 9 positions hợp lệ
- [ ] `tag.offsetX/Y` có giá trị hợp lý (< 20 feet)
- [ ] `context.density` là low/medium/high
- [ ] `tag.tagText` không rỗng
- [ ] Nếu `hasLeader=true` thì `leaderLength > 0`

---

## 7. Next Steps

1. **Bạn cung cấp**: 
   - File Revit (.rvt) hoặc export từ các project mẫu
   - Hoặc cho tôi biết project nào có sẵn trong repo

2. **Tôi sẽ tạo**:
   - Export tool trong SmartTag để tự động extract data
   - Validation script để check data quality

3. **Cùng nhau**:
   - Review và annotate data
   - Train model

---

## 8. Ví dụ File Hoàn Chỉnh

Xem file mẫu tại:
- `annotated/sample_pipes.json` - 3 samples cho pipes
