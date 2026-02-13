# SmartTag Training Data Repository

Thư mục này chứa tất cả rules, patterns, feedback để:
1. Cải thiện rule-based algorithms (Option C)
2. Chuẩn bị dữ liệu training cho ML model (Option B)

## Naming Convention Standard

**QUYẾT ĐỊNH (2026-02-13):**
- **German Standard** (MunichRE style): PWC-TWK, SW, ZUL, HZG, KLT
- **Separator**: Underscore `_` (e.g., `SW_DN100`, `L_ZUL-RLT20_DN125`)
- **Elevation Datum**: Theo discipline
  - Water Supply: `RA = [value] m UKRD` (Rohrachse, Unterkante Rohdecke)
  - Drainage: `RS = [value] m (bez. ±0.00)` (Rohrsohle, reference to ground)
  - HVAC: `RA = [value] m UKRD`
- **Dual-mode Equipment**: Phân biệt theo hệ thống đấu nối (HZG vs KLT)

## Data Sources

| Project | Drawings | Discipline | Scale | Patterns Extracted |
|---------|----------|------------|-------|-------------------|
| Munich Arena (TP1_TUV) | PG_0EG | MEP Openings | 1:100 | Opening tags (BDB/WDB), Chain dimensions |
| Munich Arena (TP1_TUV) | PG_2OG, PG_5OG | HVAC/RLTA | 1:200 | Room tags, HVAC equipment (legacy RN/RS/CN) |
| Munich Arena (TP1_TUV) | PG_3OG, PG_5OG | Sanitary/SANA | 1:100 | Pipe tags (legacy System-DN) |
| Munich Arena (TP1_TUV) | PG_2OG, PG_5OG | Heating/Cooling (HZGA) | 1:100 | HVAC piping (CHW/LTHW), Equipment (legacy) |
| Munich Arena (TP1_TUV) | PG_0EG-6OG (21 files) | Electrical/ELTR | 1:100/1:200 | Cable trays (E30/E90/AV), Panels (UV/SV/NSHV) |
| Munich Arena (TP1_TUV) | PG_1UG-6OG (25 files) | Lightning/EBLI | 1:100 | PAS, Fundamenterder, Ringerder, Blitzfanganlage |
| Munich Arena (TP1_TUV) | PG_UEB-6OG (24 files) | Electrical Install/ELEA | 1:100 | EE lighting codes, SAA, RWA, Sensors |
| MunichRE Revitalization | U2-05 (24 files) | HVAC/RLT Ventilation | 1:50 | Duct tags (L_ZUL/ABL-RLT##_DN##), Air terminals |
| MunichRE Revitalization | U2-05 (24 files) | HZG/Heating | 1:50 | Pipe tags (HZG-[System]-VL/RL_DN##), Radiators |
| MunichRE Revitalization | U2-05 (24 files) | SAN/Sanitary | 1:50 | Drainage (SW/SWE/SWF/RW), Water supply (PWC/PWH) |
| MunichRE Revitalization | U2-05 (24 files) | KLT/Refrigeration | 1:50 | Cooling pipes (KLT-IT/HKS/GLK), FCUs |

**Total Samples:** ~5600+ tag/dimension observations

## Cấu trúc thư mục

```
Data/
├── README.md                    # File này
├── Schema/                      # JSON Schema definitions
│   ├── TaggingRule.schema.json
│   ├── DimensionPattern.schema.json
│   └── Feedback.schema.json
├── Rules/                       # Rule definitions
│   ├── Tagging/
│   │   │
│   │   │── # === SANITARY (Priority 85/75) ===
│   │   ├── sanitary_drainage.json      # SW/SWE/SWF/RW drainage (German)
│   │   ├── sanitary_water_supply.json  # PWC-TWK/PWH-TWW/TWZ water (German)
│   │   ├── sanitary_fixtures.json      # WC, Waschtisch, Bodenablauf
│   │   │
│   │   │── # === HVAC (Priority 87-88) ===
│   │   ├── hvac_ducts.json             # L_ZUL/ABL-RLT (German RLT)
│   │   ├── hvac_air_terminals.json     # Tellerventile, Lüftungsgitter (German)
│   │   ├── hvac_accessories.json       # BSK, RSK, SD, KVR, VVR
│   │   ├── hvac_piping.json            # CHW/LTHW piping
│   │   │
│   │   │── # === HEATING - HZG Connected (Priority 84-85) ===
│   │   ├── heating_piping.json         # HZG-HK/HKS/UFK/HKD pipes
│   │   ├── heating_equipment.json      # Radiators, valves (HZG-connected)
│   │   │
│   │   │── # === COOLING - KLT Connected (Priority 83-85) ===
│   │   ├── refrigeration_piping.json   # KLT-IT/HKS/GLK/UFK pipes
│   │   ├── refrigeration_equipment.json # FCUs, valves (KLT-connected)
│   │   │
│   │   │── # === ELECTRICAL (Priority 89-91) ===
│   │   ├── cable_trays.json            # Kabeltrassen (E30/E90/AV/FM)
│   │   ├── electrical_panels.json      # UV, SV, NSHV, Trafo, Busway
│   │   ├── lighting_fixtures.json      # EE lighting code system
│   │   ├── fire_safety_devices.json    # SAA, RWA, smoke detectors
│   │   ├── lightning_protection.json   # PAS, Blitzschutz, Erder
│   │   ├── room_controls.json          # ZT/RT/AT, sensors, REV panels
│   │   │
│   │   │── # === GENERAL/FALLBACK (Priority 40-70) ===
│   │   ├── mep_openings.json           # BDB/WDB openings
│   │   ├── mep_equipment.json          # General MEP equipment
│   │   ├── mep_pipes_ducts.json        # FALLBACK - no tagFormat
│   │   ├── mechanical_equipment.json   # FALLBACK - legacy English
│   │   └── room_tags_german.json       # Room Name/NO format
│   │
│   └── Dimension/
│       ├── opening_chains.json         # Chain dimensions
│       └── opening_sizes.json          # Opening size dimensions
│
├── Patterns/
│   ├── TagPositions/
│   │   ├── mep_opening_tags_tuv.json
│   │   ├── hvac_ductwork_tuv.json
│   │   ├── sanitary_piping_tuv.json
│   │   ├── hvac_piping_tuv.json
│   │   ├── cable_tray_tuv.json
│   │   ├── lightning_earthing_tuv.json
│   │   ├── electrical_installation_tuv.json
│   │   ├── hvac_rlt_munichre.json      # LP5 detailed patterns
│   │   ├── heating_hzg_munichre.json   # LP5 detailed patterns
│   │   ├── sanitary_san_munichre.json  # LP5 detailed patterns
│   │   └── refrigeration_klt_munichre.json # LP5 detailed patterns
│   └── DimensionLayouts/
│       └── radial_grid_tuv.json
│
├── Feedback/
│   ├── approved/
│   ├── rejected/
│   └── corrections/
└── Training/
    └── README.md
```

## Priority Hierarchy

| Priority | Discipline | Files |
|----------|------------|-------|
| 91 | Fire Safety | fire_safety_devices.json |
| 89 | Cable Trays | cable_trays.json |
| 88 | HVAC Ducts | hvac_ducts.json |
| 87 | Air Terminals | hvac_air_terminals.json |
| 86 | HVAC Accessories | hvac_accessories.json |
| 85 | Sanitary Pipes, HVAC Pipes, Electrical Panels, Lighting | sanitary_*.json, hvac_piping.json, electrical_panels.json, lighting_fixtures.json |
| 84 | Heating Equipment | heating_equipment.json |
| 83 | Refrigeration Equipment | refrigeration_equipment.json |
| 80 | Room Controls | room_controls.json |
| 75 | Fixtures, Lightning Protection | sanitary_fixtures.json, lightning_protection.json |
| 70 | Mechanical Equipment (fallback) | mechanical_equipment.json |
| 40 | Generic Pipes/Ducts (fallback) | mep_pipes_ducts.json |

## Elevation Reference by Discipline

| Discipline | Type | Datum | Format |
|------------|------|-------|--------|
| Water Supply | RA (centerline) | UKRD | `RA = -0.17 m UKRD` |
| Drainage | RS (invert) | bez. ±0.00 | `RS = -0.94 m (bez. ±0.00)` |
| HVAC Piping | RA | UKRD | `RA = -0.27 m UKRD` |
| Condensate | RS | bez. ±0.00 | `RS = 2.53 m (bez. ±0.00)` |

## Workflow

### 1. Thu thập dữ liệu
- Tool chạy → ghi log placements/dimensions
- User review → approve/reject/correct
- Feedback được lưu tự động

### 2. Refine Rules (Option C)
- Phân tích feedback patterns
- Update rule weights/thresholds
- Test với new data

### 3. Prepare Training (Option B - sau này)
- Convert feedback → training samples
- Extract features từ Revit context
- Train ML model

## Cách cập nhật

### Thêm Rule mới
1. Tạo file JSON trong `Rules/[Category]/`
2. Follow schema trong `Schema/`
3. Tool sẽ auto-load khi khởi động

### Thêm Pattern mới
1. Export từ Revit drawings
2. Lưu vào `Patterns/[Type]/`
3. Include metadata (project, view type, etc.)

### Ghi Feedback
- Tự động qua UI (Approve/Reject buttons)
- Hoặc manual: thêm file vào `Feedback/`

## Data Format Convention

- Tất cả JSON files sử dụng UTF-8
- Coordinates: Revit internal units (feet)
- IDs: Element ID as long integer
- Timestamps: ISO 8601 format
- Separator: Underscore `_` cho tag patterns
- Categories: Sử dụng `OST_` prefix nhất quán

---

## Competitor Analysis: BIMLOGIQ Smart Annotation

**Reference:** https://bimlogiq.com/product/smart-annotation

### So sánh tính năng (2026-02-13)

| Tính năng | BIMLOGIQ | SmartTag | Gap |
|-----------|----------|----------|-----|
| **Pricing** | $125/month ($1,500/year) | Free | ✅ Advantage |
| **Processing** | Cloud (AWS) | 100% Local | ✅ Advantage (offline, privacy) |
| **Quick Mode** | Cloud AI | Local heuristics | ✅ Equal |
| **Full Mode (AI)** | Trained ML model | Rule-based | ❌ Need upgrade |
| **German MEP Standards** | ❌ | ✅ Built-in | ✅ Advantage |
| **Auto Tag** | ✅ | ✅ | ✅ Equal |
| **Auto Dimension** | ✅ | ✅ | ✅ Equal |
| **Collision Avoidance** | ✅ | ✅ | ✅ Equal |
| **Tag Alignment** | ✅ | ✅ | ✅ Equal |
| **Linear Element Split** | ? | ✅ | ✅ Advantage |
| **Tag Rotation (0°/90°)** | ? | ✅ | ✅ Advantage |
| **Linked File Tagging** | ✅ | ❌ | ❌ Gap |
| **Batch Processing** | ✅ Multiple views | ❌ Single view | ❌ Gap |
| **Custom Leader Shapes** | ✅ | ❌ | ❌ Gap |
| **Fabrication Parts** | ✅ | ❌ | ❌ Gap |
| **GUI Template Editor** | ✅ | ❌ JSON only | ❌ Gap |

### Estimated Feature Parity: ~70-80%

### Roadmap để Close Gap

| Priority | Feature | Effort | Phase |
|----------|---------|--------|-------|
| **High** | Cloud AI (Full Mode) | Large | Phase 7 - Option B |
| **Medium** | Linked File Tagging | Medium | Phase 8 |
| **Medium** | Batch Processing (Multi-view) | Medium | Phase 8 |
| **Medium** | GUI Template Editor | Medium | Phase 9 |
| **Low** | Custom Leader Shapes | Small | Phase 9 |
| **Low** | Fabrication Parts Support | Small | Phase 9 |

### SmartTag Unique Advantages

1. **100% Free** - No subscription required
2. **100% Offline** - No internet, no data privacy concerns
3. **German MEP Standards** - Built-in rules for HZG, KLT, SAN, RLT, ELTR, EBLI, ELEA
4. **Open Rule System** - JSON-based, easily customizable
5. **Linear Element Handling** - Auto-split tags for long pipes/ducts
6. **Training Data Ready** - Structure prepared for future ML training
7. **pyRevit Integration** - Lightweight, no .addin installation
