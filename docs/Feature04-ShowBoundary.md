# Feature 04: Show Boundary

Hiển thị bounding box và các điểm đặc biệt của elements trong Revit viewport.

## Tổng quan

Feature này cho phép user visualize:
- **Bounding Box**: Khung bao quanh element (world-aligned hoặc rotated)
- **Min Point**: Điểm có tọa độ nhỏ nhất (đỏ)
- **Max Point**: Điểm có tọa độ lớn nhất (xanh lá)
- **Centroid**: Tâm của bounding box (vàng)

Tất cả graphics là **temporary** - tự động biến mất khi đóng window.

## Cách sử dụng

### Chọn elements

1. **Chọn trước**: Chọn elements trong Revit, sau đó mở feature
2. **Chọn trong tool**: Click "Pick Elements" để chọn từ Revit model
3. **Auto-update**: Selection thay đổi trong Revit → preview tự động cập nhật

### Hiển thị Bounding Box

- Toggle "Show Bounding Box" để bật/tắt
- **Origin (World-aligned)**: Box căn theo trục X-Y-Z của project
- **Rotated**: Box xoay theo hướng của element (FamilyInstance, Wall với rotation)
- Có thể chọn màu và độ dày line

### Hiển thị Points

- Toggle riêng cho từng loại: Min, Max, Centroid
- Points được render dạng sphere
- Có thể chọn màu và diameter (mm) cho spheres

## Kiến trúc

```
┌─────────────────────────────────────────────────────────────┐
│                     BoundaryWindow                          │
│  (WPF UI - không gọi Revit API trực tiếp)                  │
│                                                             │
│  • Callbacks được set bởi CommonFeatureHandler             │
│  • DispatcherTimer để monitor selection changes            │
└──────────────────────────┬──────────────────────────────────┘
                           │ Callbacks
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                  CommonFeatureHandler                       │
│  (ExecuteShowBoundary - tạo và quản lý các components)     │
│                                                             │
│  • Tạo BoundaryGraphicsServer + BoundaryExternalHandler    │
│  • Set callbacks cho BoundaryWindow                        │
│  • Cleanup khi window đóng                                 │
└──────────────────────────┬──────────────────────────────────┘
                           │ ExternalEvent.Raise()
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                 BoundaryExternalHandler                     │
│  (IExternalEventHandler - chạy trên Revit main thread)     │
│                                                             │
│  Request types:                                            │
│  • PickElements: Selection.PickObjects()                   │
│  • UpdatePreview: Calculate bounds → Update graphics       │
│  • ClearPreview: Remove all graphics                       │
└──────────────────────────┬──────────────────────────────────┘
                           │ UpdateData()
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                  BoundaryGraphicsServer                     │
│  (IDirectContext3DServer - render 3D graphics)             │
│                                                             │
│  • Đăng ký với Revit's ExternalServiceRegistry            │
│  • Render lines (bounding box) và triangles (spheres)      │
│  • Thread-safe với lock                                    │
└─────────────────────────────────────────────────────────────┘
```

## File Structure

```
src/CommonFeature/
├── Models/
│   └── BoundaryModels.cs          # DTOs: BoundaryDisplaySettings, ElementBoundaryData
├── Handlers/
│   └── BoundaryExternalHandler.cs # IExternalEventHandler cho Boundary operations
├── Graphics/
│   └── BoundaryGraphicsServer.cs  # DirectContext3D server render graphics
├── Views/
│   ├── BoundaryWindow.xaml        # UI layout
│   └── BoundaryWindow.xaml.cs     # UI logic + callbacks
└── CommonFeatureHandler.cs        # Entry point (ExecuteShowBoundary)
```

## Data Flow

### Update Preview Flow

```
1. User toggles "Show Bounding Box" ON
   ↓
2. BoundaryWindow.UpdatePreviewIfNeeded()
   - Build BoundaryDisplaySettings DTO
   - Call UpdatePreviewCallback
   ↓
3. CommonFeatureHandler (callback)
   - handler.SetRequest(UpdatePreview, settings)
   - externalEvent.Raise()
   ↓
4. Revit calls BoundaryExternalHandler.Execute()
   - Lấy elements từ document
   - Tính BoundingBox, transform, centroid cho mỗi element
   - graphicsServer.UpdateData(dataList, settings)
   ↓
5. BoundaryGraphicsServer.RenderScene()
   - Rebuild geometry nếu cần
   - FlushBuffer để render
```

### Selection Auto-Update Flow

```
1. DispatcherTimer tick (500ms interval)
   ↓
2. GetCurrentSelectionCallback() → lấy selection từ Revit
   ↓
3. So sánh hash với _lastSelectionHash
   ↓
4. Nếu khác → UpdatePreviewIfNeeded()
```

## Models

### BoundaryDisplaySettings

Settings từ UI để render graphics:

| Property | Type | Mô tả |
|----------|------|-------|
| ElementIds | List<long> | IDs của elements cần hiển thị |
| ShowBoundingBox | bool | Hiển thị bounding box lines |
| UseRotatedBoundingBox | bool | Xoay theo hướng element |
| ShowMinPoint/MaxPoint/Centroid | bool | Hiển thị các điểm |
| BoundingBoxColor | Color | Màu của bounding box |
| Min/Max/CentroidColor | Color | Màu của các điểm |
| LineThickness | int | Độ dày line (1-10) |
| SphereDiameterMm | int | Đường kính sphere (20-500mm) |

### ElementBoundaryData

Dữ liệu đã tính cho một element:

| Property | Type | Mô tả |
|----------|------|-------|
| ElementId | long | ID của element |
| BoundingBox | BoundingBoxXYZ | World-aligned bounding box |
| RotationTransform | Transform | Transform để xoay (nếu có) |
| MinPoint/MaxPoint/Centroid | XYZ | Các điểm đặc biệt |

## DirectContext3D Notes

### Compatibility

- API ổn định từ Revit 2023-2026
- Hoạt động trong: 3D, FloorPlan, CeilingPlan, Section, Elevation views

### Geometry

**Bounding Box** (12 lines):
- 4 cạnh bottom face
- 4 cạnh top face  
- 4 cạnh vertical

**Sphere** (UV sphere):
- 8 latitude segments × 12 longitude segments
- ~192 triangles per sphere

### Memory

- VertexBuffer và IndexBuffer được dispose khi:
  - Data thay đổi (UpdateData)
  - Server unregister
- Maximum 500 elements để tránh memory issues

## Robustness

### Guards

- **Null checks**: doc, uidoc, element, boundingBox
- **Validity checks**: elem.IsValidObject, doc.IsValidObject
- **Zero-size bbox**: bbox.Min.IsAlmostEqualTo(bbox.Max)
- **Element limit**: Max 500 elements với warning

### Document Change Detection

Nếu user switch document khi BoundaryWindow đang mở:
- Detect qua PathName/Title comparison
- Show warning message
- Auto-close window

### Error Handling

- Try-catch quanh từng element trong loop
- Elements lỗi được skip, không crash toàn bộ feature
- Cleanup errors được ignore (không block window close)

## Known Limitations

1. **Line thickness**: DirectContext3D không support variable line thickness (hardcoded = 1 pixel)
2. **Rotated bbox cho Wall**: Chỉ có effect với walls có rotation, straight walls không có visual difference
3. **Linked elements**: Elements từ linked files được skip
4. **Large selections**: UI có thể lag nếu > 500 elements

## Future Improvements

1. Add "Export coordinates to CSV" button
2. Support linked model elements
3. Add dimension labels on bounding box
4. Real line thickness support (nếu Revit API hỗ trợ trong tương lai)
