# Training Data Requirements for SmartTag ML

## Overview

To train the ML placement system, training data must be collected from professionally tagged drawings.

---

## 1. Data to Collect

### 1.1 From Revit Model (Automatic Export)

| Data Type | Description | Format |
|-----------|-------------|--------|
| **Element data** | Information about tagged elements | JSON |
| **Tag data** | Actual tag positions | JSON |
| **View data** | Scale, crop box | JSON |
| **Context data** | Neighbors, density | JSON |

### 1.2 Files to Export per View

```
📁 training_export_<project>_<view>/
├── elements.json       # All elements in the view
├── tags.json           # All placed tags
├── view_info.json      # View scale, crop, type
└── screenshot.png      # Screenshot for verification (optional)
```

---

## 2. Detailed Schema for Training Data

### 2.1 Main File: `annotated_data.json`

```json
{
  "version": "1.0",
  "source": {
    "project": "MunichRE Revitalization",
    "discipline": "Sanitary",
    "drawings": ["SAN_01_0001", "SAN_01_0002"],
    "viewScale": 50,
    "annotatedBy": "Annotator name",
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
    "category": "OST_PipeCurves",
    "familyName": "Pipe Types",
    "typeName": "Standard",
    "orientation": 0,
    "isLinear": true,
    "length": 15.5,
    "width": 0.33,
    "height": 0.33,
    "diameter": 0.33,
    "systemType": "Domestic Cold Water",
    "centerX": 10.5,
    "centerY": 25.3
  }
}
```

### 2.3 Context Data

```json
{
  "context": {
    "density": "medium",
    "neighborCount": 3,
    "hasNeighborAbove": true,
    "hasNeighborBelow": false,
    "hasNeighborLeft": false,
    "hasNeighborRight": true,
    "distanceToNearestAbove": 2.5,
    "distanceToNearestBelow": 10.0,
    "distanceToNearestLeft": 8.0,
    "distanceToNearestRight": 3.0,
    "distanceToWall": 5.0,
    "parallelElementsCount": 2,
    "isInGroup": false
  }
}
```

### 2.4 Tag Data (Ground Truth)

```json
{
  "tag": {
    "position": "BottomCenter",
    "offsetX": 0,
    "offsetY": -1.5,
    "hasLeader": false,
    "leaderLength": 0,
    "rotation": "Horizontal",
    "alignedWithRow": true,
    "alignedWithColumn": false,
    "rowId": "row_01",
    "columnId": null,
    "tagText": "PWC-TWK DN100",
    "tagWidth": 2.5,
    "tagHeight": 0.8
  }
}
```

---

## 3. Reference Projects for Data Collection

### 3.1 General Requirements
- **Professional drawings** tagged according to German/European standards
- **Multiple scales**: 1:50, 1:100, 1:200
- **Multiple disciplines**: Sanitary, HVAC, Electrical, Plumbing

### 3.2 Target Sample Counts

| Discipline | Scale 1:50 | Scale 1:100 | Scale 1:200 | Total |
|------------|------------|-------------|-------------|-------|
| Sanitary (Pipes) | 150 | 100 | 50 | 300 |
| HVAC (Ducts) | 150 | 100 | 50 | 300 |
| Electrical | 100 | 100 | 50 | 250 |
| Cable Tray | 100 | 100 | 50 | 250 |
| Equipment | 50 | 50 | 50 | 150 |
| **Total** | **550** | **450** | **250** | **1250** |

---

## 4. Data Collection Methods

### 4.1 Automatic Export from Revit

An automatic export tool will be available in SmartTag:
1. Open a well-tagged view
2. Run "Export Training Data"
3. The tool automatically extracts element + tag + context

### 4.2 Manual Annotation (if needed)

If tags are not yet placed, manual annotation is required:
1. Export elements from the view
2. Use the Annotation Tool to mark ideal tag positions
3. The tool automatically calculates offset and context

---

## 5. File Structure

```
📁 src/SmartTag/Data/Training/
├── 📁 annotated/                    # Annotated training data
│   ├── munichre_sanitary_50.json    # MunichRE, Sanitary, 1:50
│   ├── munichre_hvac_50.json
│   ├── arena_electrical_100.json
│   ├── arena_cabletray_100.json
│   └── ...
├── 📁 feedback/                     # User feedback (continuous learning)
│   ├── feedback_2026-02-13.json
│   └── ...
├── 📁 raw_exports/                  # Raw exports from Revit (not yet annotated)
│   └── ...
└── TRAINING_DATA_REQUIREMENTS.md    # This file
```

---

## 6. Validation Checklist

Each sample must pass these checks:

- [ ] `element.category` is a valid BuiltInCategory
- [ ] `element.centerX/Y` is within view bounds
- [ ] `tag.position` is one of 9 valid positions
- [ ] `tag.offsetX/Y` has a reasonable value (< 20 feet)
- [ ] `context.density` is low/medium/high
- [ ] `tag.tagText` is not empty
- [ ] If `hasLeader=true` then `leaderLength > 0`

---

## 7. Next Steps

1. **You provide**:
   - Revit files (.rvt) or exports from reference projects
   - Or let me know which projects are already in the repo

2. **I will create**:
   - Export tool in SmartTag for automatic data extraction
   - Validation script to check data quality

3. **Together**:
   - Review and annotate data
   - Train model

---

## 8. Complete File Example

See the sample file at:
- `annotated/sample_pipes.json` - 3 samples for pipes
