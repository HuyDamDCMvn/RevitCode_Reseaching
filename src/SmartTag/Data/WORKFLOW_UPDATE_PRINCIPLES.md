# Updating Principles (Rules/Patterns) from Your Drawings

## Priority Routing

The tool always prioritizes **internal rules and patterns** first, then uses user-exported data (if available):

1. **Internal Rules** — `Data/Rules/Tagging/*.json` (highest priority)
2. **Internal Patterns** — `Data/Patterns/TagPositions/*.json`
3. **Learned (user export)** — `Data/Training/learned_overrides.json` (only used when no matching rule/pattern exists, or to supplement offset/leader when rules don't specify)

This means: if a rule/pattern already exists for that category, it is always used; learned data is only consulted when no rule/pattern matches or when parameters are missing.

---

## Auto-Learning on Export (No AI Prompt Needed)

When you click **"Export training data from view"** in Revit:

1. The tool exports JSON to `Data/Training/annotated/`.
2. **Automatically**: The tool reads back the exported file, groups by **category** (and **category+system**), and calculates:
   - Most common tag position → `preferredPositions`
   - Whether most tags have leaders → `addLeader`
   - Average offset distance → `offsetDistance`
3. Results are written to **`Data/Training/learned_overrides.json`** (original Rules/Patterns files are not modified).
4. Next time tags are placed: **Rule** → **Pattern** is used first; only when missing does it fall back to **Learned** from the file above.

You **do not need to prompt the AI** to update: just export from a well-tagged view whenever you want to add your own tag placement preferences; internal rules/patterns still take priority.

---

## Can I Read .rvt Files?

**No.** In this environment (Cursor/IDE), there is no running Revit instance, and `.rvt` files are Revit's proprietary binary format — only readable by Revit or Autodesk Forge API.
Therefore, **uploading .rvt files to the repo does not allow me to read and update principles automatically**.

## Correct Approach: Export in Revit → Add JSON to Repo → I Update

### Step 1: In Revit

1. Open your **.rvt** file in Revit.
2. Open a **view** that has been properly tagged (floor plan / ceiling plan / section with many tags).
3. Open **Smart Tag** (pyRevit → Smart Tag).
4. Click **"Export training data from view"**.
5. The tool will:
   - Collect all elements **that already have tags** in the view
   - Write a JSON file (element + context + actual tag position)
   - Save to the **Data/Training/annotated/** folder (next to the DLL or dev path)

### Step 2: Add the JSON File to the Repo

- If building from the repo and running from `HD.extension`, the exported file may already be in:
  - `src/SmartTag/Data/Training/annotated/exported_<ViewName>_<date>.json`
- Copy that file into the repo (or ensure it's in the workspace).
- Commit and push if you want me to read it from the repo.

### Step 3: Ask Me to Update Principles

In chat, write clearly:

- **"Update principles from file exported_xxx.json"**
  or
- Attach / paste the JSON file path (e.g., `Data/Training/annotated/exported_FloorPlan_20260213_1430.json`).

I will:

- Read the exported JSON file,
- Analyze by category, tag position, offset, hasLeader,
- Propose (or generate) updates to:
  - **Rules** (`Data/Rules/Tagging/*.json`): `preferredPositions`, `offsetDistance`, `addLeader`, etc.
  - **Patterns** (`Data/Patterns/TagPositions/*.json`): add/update `observations` with `position`, `hasLeader` matching your drawings.

## Summary

| You Do | Can I Do It? |
|--------|--------------|
| Upload **.rvt** file | No — cannot read .rvt |
| Export in Revit → **.json** file → add to repo | Yes — read JSON and update Rules/Patterns |

**Workflow:** Open .rvt in Revit → Well-tagged view → Smart Tag → **Export training data from view** → Add JSON file to repo → Tell me **"update principles from file &lt;filename&gt;"**.
