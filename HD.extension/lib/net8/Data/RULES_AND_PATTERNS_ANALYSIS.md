# Rules & Patterns in the Repo — Are They Usable?

## Summary

| Type | Path | Currently Used in Code? | Usable for ML? |
|------|------|------------------------|----------------|
| **Rules (Tagging)** | `Data/Rules/Tagging/*.json` | **Yes** — RuleEngine + TagPlacementService | **Yes** — Preferred positions, offset, scoring |
| **Rules (Dimension)** | `Data/Rules/Dimension/*.json` | **Yes** — RuleEngine | Not used for tag placement |
| **Patterns (TagPositions)** | `Data/Patterns/TagPositions/*.json` | **Yes** — TagPositionPatternLoader | **Partially** — position text → TagPosition, used when no Rule matches |

---

## 1. Rules — Currently Used and Fully Usable

### 1.1 Where Loaded and Used

- **RuleEngine** (`Services/RuleEngine.cs`) loads all files from `Data/Rules/Tagging/*.json` during `Initialize()`.
- **TagPlacementService** uses Rules for:
  - `GetPreferredPositionsFromRule(element)` → list of preferred positions (TopCenter, BottomCenter, etc.).
  - `GetRuleSettings(element)` → offset, addLeader, collisionPenalty, preferenceBonus, alignmentBonus, avoidCollisionWith, groupAlignment.

### 1.2 Rule Content (Placement-Related)

In each rule file (e.g., `mep_pipes_ducts.json`, `sanitary_drainage.json`):

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

- **preferredPositions**: directly usable for placement (and already used in TagPlacementService).
- **offsetDistance, addLeader, avoidCollisionWith, groupAlignment**: usable for tag placement logic and CSP.
- **scoring**: usable for candidate scoring (already used in TagPlacementService).

**Conclusion:** Rules **are usable** and **are already being used** in placement. The ML pipeline (PlacementEngine) should **additionally call RuleEngine** to:
- Sort/add candidates by `preferredPositions`,
- Apply `offsetDistance` / `addLeader` / `avoidCollisionWith` when creating and scoring candidates.

---

## 2. Patterns (TagPositions) — Partially Usable

### 2.1 Current Structure

- Located in `Data/Patterns/TagPositions/` (e.g., `hvac_rlt_munichre.json`, `sanitary_san_munichre.json`).
- Each file contains:
  - **source**: project, drawings, discipline, scale.
  - **legend**: system annotations (ZUL, ABL, SW, PWC-TWK, etc.).
  - **observations**: array describing tag placement:
    - `elementType`: "Duct Tag - Round", "Waste Pipe Tag - Horizontal", etc.
    - `labelingPattern` / `labelingPatterns`: text format (not position).
    - `position`: **descriptive string**, e.g.:
      - `"Along duct centerline"`
      - `"Above/Below pipe"`
    - `examples`: sample tag text.

- **RuleEngine only loads Rules/Tagging and Rules/Dimension**, **not** the `Patterns/TagPositions` directory.

### 2.2 Implemented Loader (TagPositionPatternLoader)

- **Services/TagPositionPatternLoader.cs** loads all files in `Data/Patterns/TagPositions/*.json` (excluding files starting with `_`).
- Each observation may have:
  - **position** (text, e.g., "Along duct centerline") → mapped to TagPosition enum.
  - **tagPosition** (enum directly, e.g., "Left", "Center").
  - **hasLeader** (bool) → used for leader suggestions.
- Matches by category + discipline (from filename) + scale (from source).
- **TagPlacementService** and **PlacementEngine** both call the pattern loader when no matching rule exists: preferred positions are sourced from patterns.

### 2.3 Comparison with Training Schema (ML)

Training samples need: **element** (category, orientation, size, etc.) + **context** (density, neighbors, etc.) + **tag** (position enum, offsetX, offsetY, hasLeader, etc.).

Current patterns have:
- Available: elementType (close to category), source (project, scale), position **as text**.
- Missing: offsetX/offsetY, context (density, neighbors), bounding box.

**Conclusion:**
- **Cannot** directly use Patterns as fully qualified training samples (missing offset, context).
- **Can use partially** by:
  - Adding a loader to read `Data/Patterns/TagPositions/*.json`.
  - Mapping `position` strings → TagPosition enum, e.g.:
    - "Along duct centerline", "Along centerline" → **Center**
    - "Above", "Above pipe" → **TopCenter** / **TopRight**
    - "Below" → **BottomCenter** / **BottomLeft**
  - Using as **position preference hints** by discipline/scale (similar to Rules), or as **pseudo-samples** for KNN (with default context/offset).

---

## 3. Recommendations

### 3.1 Rules

- **Keep** the current usage in TagPlacementService.
- **Extend**: have **PlacementEngine** (ML pipeline) call RuleEngine to:
  - Get `preferredPositions` and `GetRuleSettings` when creating/sorting candidates and when running CSP.

This way Rules are both utilized and maximally leveraged for both heuristic and ML approaches.

### 3.2 Patterns (TagPositions)

- **Option A (quick):** Add a loader for Patterns, map `observations[].position` (text) → TagPosition, and use as a "position preference" source by project/discipline/scale (supplementing Rules).
- **Option B (more complete):** When Revit exports are available (TrainingDataExporter), create a script to convert Patterns → training file with "pseudo-samples" (position + default context) so KNN has more data, noting low confidence.

---

## 4. Reference Files in the Repo

| Purpose | File |
|---------|------|
| Load Rules | `Services/RuleEngine.cs` (Tagging + Dimension) |
| Use Rules in placement | `Services/TagPlacementService.cs` (GetPreferredPositionsFromRule, GetRuleSettings) |
| Rule Schema | `Data/Schema/TaggingRule.schema.json` |
| Example Rules | `Data/Rules/Tagging/mep_pipes_ducts.json`, `sanitary_drainage.json`, etc. |
| Patterns | `Data/Patterns/TagPositions/*.json`, `_template.json` |
| Pattern Template | `Data/Patterns/TagPositions/_template.json` |

---

**Overall Conclusion:**
- **Rules:** Currently used, fully usable for placement, and should be additionally integrated into PlacementEngine.
- **Patterns:** Partially usable (position text → position preference or pseudo training) if a loader and mapping are added.
