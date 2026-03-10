# Tag Placement Decision Flow

This document describes how the Smart Tag algorithm decides **where to place a tag** for each element in a view.

---

## Overview

The flow consists of **4 main phases**:

1. **Preparation** — Load rules/patterns/learned data, index elements & obstacles
2. **Position Selection** — Generate candidates, score them, select the best position for each element
3. **Collision Resolution** — Push tags away from annotations/clearance zones, then push tag-tag to avoid overlap
4. **Alignment** — Align tags into rows/columns (if "Align tags in rows" is enabled)

---

## 1. Preparation (Handler + TagPlacementService.Initialize)

- **Handler** (Revit thread):
  - Retrieves the list of elements to tag based on the user's category selection.
  - Retrieves **existing tags** (tags already in the view) and **annotations** (dimensions, text notes, **ClearanceZones**).
- **TagPlacementService.Initialize(elements, existingTags, annotations)**:
  - Builds **spatial indices** for:
    - **Elements** (to check if a tag overlaps another element).
    - **Tags** (existing tags + newly placed tags — to avoid tag-on-tag overlap).
    - **Annotations** (dimensions, text, ClearanceZones — tags must not overlap these).
  - Calibration: tag size (width/height), leader length, min spacing (from regression + Revit API).

---

## 2. Position Selection per Element (CalculatePlacements)

### 2.1 Element Processing Order

- Filter elements in groups (via `ShouldTagGroupedElement`).
- **Sort**: by **Y descending** (top first), then **X ascending** (left first) — processes top-to-bottom, left-to-right.
- Skip elements that already have tags if the user enabled "Skip already tagged".

### 2.2 Two Element Types

- **Long linear elements** (pipes/ducts longer than threshold): **CreateLinearElementPlacements**
  - Split into multiple segments, one tag per segment along the centerline.
  - Each segment: generate candidates around the segment midpoint, score them, select the best position.
- **Everything else** (equipment, fittings, short linear elements): **FindBestPlacement**
  - One tag per element.

### 2.3 Candidate Generation (GenerateCandidatePositions)

- **Preferred positions** are sourced from (in order):
  1. **Rules** (Data/Rules/Tagging) — by category/family/system.
  2. **Patterns** (Data/Patterns/TagPositions) — by category/system, scale.
  3. **Learned** (learned_overrides.json) — from user exports.
- **Offset distance**: from rule/learned or default (leader length + tag size + spacing).
- **Candidates** = positions: TopLeft, TopCenter, TopRight, Left, Center, Right, BottomLeft, BottomCenter, BottomRight (additional farther tiers may be added if needed).
- **Linear elements** (when rule enables PreferCenterline): candidates along centerline, no leader.
- Candidates are **sorted**: preferred positions from rule/pattern/learned come first, then by distance (closer is better).

### 2.4 Scoring (ScorePlacement) — lower score = better

| Component | Calculation | Purpose |
|-----------|-------------|---------|
| **Tag-tag collision** | Very large penalty x 3^(collision count) | Prioritize no tag overlap |
| **Overlap area** | + overlap area x 200 | Minimize overlap as much as possible |
| **Tag-element** | + element collision count x ELEMENT_COLLISION_PENALTY | Avoid tag covering other elements |
| **Rule AvoidCategories** | Extra penalty if overlapping a rule-forbidden category | Respect rule "avoid" categories |
| **Tag-annotation/ClearanceZone** | + collision count x ANNOTATION_COLLISION_PENALTY | Avoid covering dimensions, text, clearance zones |
| **Leader collision** | + collision count x LEADER_COLLISION_PENALTY | Leader should not cross tags/elements |
| **User-selected position** | Preference score (based on dropdown Position) | Prioritize TopRight/Left/etc. if user selected |
| **Rule/learned preferred position** | - PreferenceBonus if matching preferred position | Prioritize rule/learned positions |
| **Leader length** | + leader length x 1 | Closer to element is slightly better |
| **Linear, no leader** | - 20 (bonus) | Prefer centerline tags without leader |
| **Distance tier** | + distance multiplier x 5 (when no collision) | Prefer closer positions |
| **Grid alignment** | - alignment score x AlignmentBonus/10 | Prefer tags on grid (1 ft) |
| **Learned alignment** | AlignmentBonus increases if learned has preferAlignRow/Column | Stronger alignment preference when learned from exports |
| **Near-edge (equipment)** | Bonus if tag is close to element edge | Keep tag from drifting too far |

### 2.5 Best Position Selection (FindBestPlacement)

- For each candidate:
  - Check **collision with tags** (in index).
  - Score using **ScorePlacement**.
- **Split** candidates into two groups: **no collision** and **has collision**.
- **Priority**: always select the **no-collision candidate with the lowest score**.
- Only when **no collision-free candidates remain** does it consider candidates with collisions (lowest score).
- If the best score still exceeds **MAX_ACCEPTABLE_SCORE** → **skip** the element (no tag placed).
- After selection, **add** that tag to the **tag index** so subsequent elements avoid overlapping it.

---

## 3. Collision Resolution (ResolveCollisions)

After generating placements for all elements:

### 3.1 Push Away from Annotations / ClearanceZones

- For each placement, check **overlap** with _annotationIndex (dimensions, text, **ClearanceZones**).
- If overlapping: **PushAwayFromAnnotation** — push the tag in the direction from the annotation center outward, far enough to clear overlap + margin.
- Repeat up to 5 iterations to stabilize.

### 3.2 Push Tags Apart (PushApart)

- Use spatial index of **placements**.
- Find pairs of placements whose **EstimatedTagBounds** intersect.
- **PushApart**: compute direction and distance needed (half-width/height + spacing), push both tags in opposite directions.
- Repeat up to 20 iterations until no more overlap.

---

## 4. Refinement Loop (RefinePlacementsIterative)

After ResolveCollisions, run **up to 3 iterations**:

1. **Re-resolve + Re-align**: call ResolveCollisions again (push annotations, push tag-tag), then AlignTagPlacements if "Align tags in rows" is enabled.
2. **Update _tagIndex**: synchronize the index with current bounds of each placement (after pushing/aligning).
3. **Scan for overlap**: find placements that still overlap another tag or annotation.
4. **Re-place**:
   - Only consider placements belonging to elements with **a single tag** (skip linear multi-segment elements).
   - For each placement still overlapping: remove it from _tagIndex, call **FindBestPlacement** again for that element (using the current tag index state); if a new position is found, replace the placement in the list and add to _tagIndex; otherwise keep the original and re-add to index.
5. If no placements still overlap → exit loop; if all 3 iterations are exhausted → stop.

Result: tags still overlapping after step 3 get a chance to be **re-placed** at a different position or **re-aligned** via Resolve/Align in subsequent iterations.

---

## 5. Alignment (AlignTagPlacements)

Only runs when the user enables **"Align tags in rows"**:

- **Rows** — group by Y (tolerance ~ 2x tag height): within each row of 2+ tags, align Y to the average if it doesn't cause collisions → tags in the same row share the same Y.
- **Columns** — based on reference patterns (EA/SA vertical stacking): group by X (tolerance ~ 2x tag width), within each column of 2+ tags, align X to the average if it doesn't cause collisions → tags in the same column stack vertically (like EA-350x250 / SA-350x250).

Scoring includes **column alignment bonus**: candidates with X close to another tag (same column, no overlap) receive a score reduction to promote vertical stacking.

---

## 6. Decision Order Summary

1. **Rule / Pattern / Learned** → preferred positions (TopRight, BottomCenter, etc.) and offset/leader/alignment bonus.
2. **Generate candidates** around element at those positions, multiple distances.
3. **Score** each candidate (collision is most penalized, then preference, alignment, distance).
4. **Select** the best **no-collision** candidate; only consider collision candidates if none are collision-free.
5. **Update tag index** so the next element avoids overlapping.
6. After selecting for all elements: **push** tags away from annotations/clearance, then **push** tag-tag apart.
7. **Refinement loop** (up to 3 iterations): re-resolve + re-align → scan placements still overlapping → **re-place** each overlapping tag (single-tag elements only) → repeat until clean or iterations exhausted.
8. If alignment is enabled: **align** tags into rows (same Y) when safe.

The entire flow ensures: **avoiding overlap** (tags, elements, dimensions, ClearanceZones) is the highest priority; only then does it optimize for **preferred position** and **alignment** based on rules/patterns/learned data and user settings.
