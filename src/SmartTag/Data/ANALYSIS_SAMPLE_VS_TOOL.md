# Analysis: Reference Drawings vs Tool Output

Comparison of reference drawings (standard) with Smart Tag output to improve the training/placement algorithm.

---

## Reference Drawings

- **Tags**: PREFIX-WIDTHxHEIGHT format (EA-350x250, SA-200x150).
- **Position**: near the duct segment, typically **above** (blue) or **above/below** (red).
- **EA/SA Pairs**: two tags **stacked vertically** (same X), one above and one below, both attached to the same segment/point.
- **Columns**: multiple tag pairs in the same X region form **vertical columns** (same X), easy to read.
- **Leaders**: short, connecting from tag to duct; some leaders cross ducts/other leaders in high-density areas.

---

## Tool Output (Before Update)

- **Align tags in rows**: tags in the same row (same Y) → neat horizontal rows.
- **Missing column alignment**: tags near each other in X were not pulled to the same X → rarely stacked vertically like EA/SA.
- High-density areas still had tags **touching** or **slightly overlapping** despite resolve/refinement.

---

## Algorithm Updates (Completed)

1. **Column alignment** in `AlignTagPlacements`:
   - After row alignment (Y), group tags by **X** (tolerance ~ 2x tag width).
   - Within each column of 2+ tags: align **X** to the average if no collision results.
   - Result: tags in the same X region stack **vertically** (same column), matching EA/SA pairs in reference drawings.

2. **"Same column" scoring bonus** in `ScorePlacement`:
   - If a candidate position has **X close to** another tag (within ~1.5 ft band) and **no overlap** → score reduction (bonus).
   - Promotes choosing positions that create **vertical stacks** with already-placed tags.

3. **HVAC duct rule** (already in place): `preferredPositions: ["Center","TopCenter","BottomCenter"]`, `groupAlignment: "AlongCenterline"` — unchanged, consistent with duct and reference drawing patterns.

---

## Expected Result

- Tags on the same duct segment tend to be **in the same column** (same X), stacked vertically.
- Rows (same Y) are still maintained via existing row alignment.
- Combined **row + column** alignment closely matches the reference drawing layout (EA/SA stacking, vertical columns).

---

## Further Training Suggestions

- **Export** a view that has been manually adjusted to match the desired column/row layout → use **Export training data from view**.
- Run **ingest** (automatic after export or `tools/ingest_annotated_to_learned.py`) to update **preferAlignRow** / **preferAlignColumn** in learned_overrides.
- Learned alignment is already used to increase **AlignmentBonus** during tag placement.
