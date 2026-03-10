# Feature 04: Show Boundary

Display bounding boxes and special points of elements in the Revit viewport.

## Overview

This feature allows users to visualize:
- **Bounding Box**: Frame around an element (world-aligned or rotated)
- **Min Point**: Point with the smallest coordinates (red)
- **Max Point**: Point with the largest coordinates (green)
- **Centroid**: Center of the bounding box (yellow)

All graphics are **temporary** — they disappear automatically when the window is closed.

## Usage

### Selecting Elements

1. **Pre-select**: Select elements in Revit, then open the feature
2. **Pick in tool**: Click "Pick Elements" to select from the Revit model
3. **Auto-update**: When selection changes in Revit, the preview updates automatically

### Displaying Bounding Box

- Toggle "Show Bounding Box" to enable/disable
- **Origin (World-aligned)**: Box aligned to the project's X-Y-Z axes
- **Rotated**: Box rotated to match the element's orientation (FamilyInstance, Wall with rotation)
- Line color and thickness are customizable

### Displaying Points

- Separate toggle for each type: Min, Max, Centroid
- Points are rendered as spheres
- Color and diameter (mm) are customizable for each sphere type

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     BoundaryWindow                          │
│  (WPF UI — does not call Revit API directly)                │
│                                                             │
│  • Callbacks set by CommonFeatureHandler                    │
│  • DispatcherTimer to monitor selection changes             │
└──────────────────────────┬──────────────────────────────────┘
                           │ Callbacks
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                  CommonFeatureHandler                       │
│  (ExecuteShowBoundary — creates and manages components)     │
│                                                             │
│  • Creates BoundaryGraphicsServer + BoundaryExternalHandler │
│  • Sets callbacks for BoundaryWindow                        │
│  • Cleanup when window closes                               │
└──────────────────────────┬──────────────────────────────────┘
                           │ ExternalEvent.Raise()
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                 BoundaryExternalHandler                     │
│  (IExternalEventHandler — runs on Revit main thread)        │
│                                                             │
│  Request types:                                             │
│  • PickElements: Selection.PickObjects()                    │
│  • UpdatePreview: Calculate bounds → Update graphics        │
│  • ClearPreview: Remove all graphics                        │
└──────────────────────────┬──────────────────────────────────┘
                           │ UpdateData()
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                  BoundaryGraphicsServer                     │
│  (IDirectContext3DServer — renders 3D graphics)             │
│                                                             │
│  • Registered with Revit's ExternalServiceRegistry          │
│  • Renders lines (bounding box) and triangles (spheres)     │
│  • Thread-safe with lock                                    │
└─────────────────────────────────────────────────────────────┘
```

## File Structure

```
src/CommonFeature/
├── Models/
│   └── BoundaryModels.cs          # DTOs: BoundaryDisplaySettings, ElementBoundaryData
├── Handlers/
│   └── BoundaryExternalHandler.cs # IExternalEventHandler for Boundary operations
├── Graphics/
│   └── BoundaryGraphicsServer.cs  # DirectContext3D server for rendering
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
   - Gets elements from document
   - Calculates BoundingBox, transform, centroid for each element
   - graphicsServer.UpdateData(dataList, settings)
   ↓
5. BoundaryGraphicsServer.RenderScene()
   - Rebuilds geometry if needed
   - FlushBuffer for rendering
```

### Selection Auto-Update Flow

```
1. DispatcherTimer tick (500ms interval)
   ↓
2. GetCurrentSelectionCallback() → gets selection from Revit
   ↓
3. Compare hash with _lastSelectionHash
   ↓
4. If different → UpdatePreviewIfNeeded()
```

## Models

### BoundaryDisplaySettings

Settings from UI for rendering graphics:

| Property | Type | Description |
|----------|------|-------------|
| ElementIds | List<long> | IDs of elements to display |
| ShowBoundingBox | bool | Show bounding box lines |
| UseRotatedBoundingBox | bool | Rotate to match element orientation |
| ShowMinPoint/MaxPoint/Centroid | bool | Show respective points |
| BoundingBoxColor | Color | Color of the bounding box |
| Min/Max/CentroidColor | Color | Color of respective points |
| LineThickness | int | Line thickness (1-10) |
| SphereDiameterMm | int | Sphere diameter (20-500mm) |

### ElementBoundaryData

Computed data for a single element:

| Property | Type | Description |
|----------|------|-------------|
| ElementId | long | Element ID |
| BoundingBox | BoundingBoxXYZ | World-aligned bounding box |
| RotationTransform | Transform | Rotation transform (if applicable) |
| MinPoint/MaxPoint/Centroid | XYZ | Special points |

## DirectContext3D Notes

### Compatibility

- API stable from Revit 2023-2026
- Works in: 3D, FloorPlan, CeilingPlan, Section, Elevation views

### Geometry

**Bounding Box** (12 lines):
- 4 bottom face edges
- 4 top face edges
- 4 vertical edges

**Sphere** (UV sphere):
- 8 latitude segments x 12 longitude segments
- ~192 triangles per sphere

### Memory

- VertexBuffer and IndexBuffer are disposed when:
  - Data changes (UpdateData)
  - Server is unregistered
- Maximum 500 elements to avoid memory issues

## Robustness

### Guards

- **Null checks**: doc, uidoc, element, boundingBox
- **Validity checks**: elem.IsValidObject, doc.IsValidObject
- **Zero-size bbox**: bbox.Min.IsAlmostEqualTo(bbox.Max)
- **Element limit**: Max 500 elements with warning

### Document Change Detection

If the user switches documents while BoundaryWindow is open:
- Detected via PathName/Title comparison
- Shows warning message
- Auto-closes window

### Error Handling

- Try-catch around each element in the loop
- Failed elements are skipped without crashing the entire feature
- Cleanup errors are ignored (do not block window close)

## Known Limitations

1. **Line thickness**: DirectContext3D does not support variable line thickness (hardcoded = 1 pixel)
2. **Rotated bbox for Wall**: Only has effect on walls with rotation; straight walls show no visual difference
3. **Linked elements**: Elements from linked files are skipped
4. **Large selections**: UI may lag if > 500 elements

## Future Improvements

1. Add "Export coordinates to CSV" button
2. Support linked model elements
3. Add dimension labels on bounding box
4. Real line thickness support (if Revit API supports it in the future)
