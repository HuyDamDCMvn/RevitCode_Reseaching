# SmartTag Training Data Repository

This folder contains all rules, patterns, and feedback for:
1. Improving rule-based algorithms (Option C)
2. Preparing training data for ML models (Option B)

## Naming Convention Standard

**Decision (2026-02-13):**
- **German Standard** (MunichRE style): PWC-TWK, SW, ZUL, HZG, KLT
- **Separator**: Underscore `_` (e.g., `SW_DN100`, `L_ZUL-RLT20_DN125`)
- **Elevation Datum**: Per discipline
  - Water Supply: `RA = [value] m UKRD` (Rohrachse, Unterkante Rohdecke)
  - Drainage: `RS = [value] m (bez. ±0.00)` (Rohrsohle, reference to ground)
  - HVAC: `RA = [value] m UKRD`
- **Dual-mode Equipment**: Differentiated by connected system (HZG vs KLT)

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

## Folder Structure

```
Data/
├── README.md                    # This file
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

### 1. Data Collection
- Tool runs and logs placements/dimensions
- User reviews and approves/rejects/corrects
- Feedback is saved automatically

### 2. Refine Rules (Option C)
- Analyze feedback patterns
- Update rule weights/thresholds
- Test with new data

### 3. Prepare Training (Option B — future)
- Convert feedback into training samples
- Extract features from Revit context
- Train ML model

## How to Update

### Adding New Rules
1. Create a JSON file in `Rules/[Category]/`
2. Follow the schema in `Schema/`
3. The tool auto-loads rules on startup

### Adding New Patterns
1. Export from Revit drawings
2. Save to `Patterns/[Type]/`
3. Include metadata (project, view type, etc.)

### Recording Feedback
- Automatically via UI (Approve/Reject buttons)
- Or manually: add files to `Feedback/`

## Data Format Convention

- All JSON files use UTF-8
- Coordinates: Revit internal units (feet)
- IDs: Element ID as long integer
- Timestamps: ISO 8601 format
- Separator: Underscore `_` for tag patterns
- Categories: Use `OST_` prefix consistently

---

## Competitor Analysis: BIMLOGIQ Smart Annotation

**Reference:** https://bimlogiq.com/product/smart-annotation

### Feature Comparison (2026-02-13)

| Feature | BIMLOGIQ | SmartTag | Gap |
|---------|----------|----------|-----|
| **Pricing** | $125/month ($1,500/year) | Free | Advantage |
| **Processing** | Cloud (AWS) | 100% Local | Advantage (offline, privacy) |
| **Quick Mode** | Cloud AI | Local heuristics | Equal |
| **Full Mode (AI)** | Trained ML model | Rule-based | Need upgrade |
| **German MEP Standards** | No | Built-in | Advantage |
| **Auto Tag** | Yes | Yes | Equal |
| **Auto Dimension** | Yes | Yes | Equal |
| **Collision Avoidance** | Yes | Yes | Equal |
| **Tag Alignment** | Yes | Yes | Equal |
| **Linear Element Split** | ? | Yes | Advantage |
| **Tag Rotation (0/90)** | ? | Yes | Advantage |
| **Linked File Tagging** | Yes | No | Gap |
| **Batch Processing** | Yes (Multiple views) | No (Single view) | Gap |
| **Custom Leader Shapes** | Yes | No | Gap |
| **Fabrication Parts** | Yes | No | Gap |
| **GUI Template Editor** | Yes | No (JSON only) | Gap |

### Estimated Feature Parity: ~70-80%

### Roadmap to Close Gap

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
