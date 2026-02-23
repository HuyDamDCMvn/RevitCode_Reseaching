# Release v2.1.0 — Major Upgrade

**Date:** 2026-02-23
**Tag:** `v2.1.0`
**Commits since v2.0.3:** 15

---

## What's New

### 14 New Tools

| Tool | Category | Description |
|------|----------|-------------|
| `get_element_geometry` | Core | Volume, area, length, bounding box in metric units |
| `find_elements_near` | Core | Spatial query: find elements within radius |
| `get_wall_layers` | Core | Wall compound structure: layers, materials, thickness |
| `get_empty_tags` | Modeler | Find tags displaying no text (QC check) |
| `align_tags` | Modeler | Align multiple tags (left/right/center/top/bottom) |
| `check_insulation_coverage` | MEP | Find pipes/ducts missing insulation |
| `check_velocity` | MEP | Flag elements exceeding max flow velocity |
| `override_color_by_system` | ViewControl | Color-code MEP by system name (12-color palette) |
| `override_color_by_parameter` | ViewControl | Heat map by numeric parameter value |
| `set_view_range` | ViewControl | Set cut plane/top/bottom for plan views |
| `screenshot_view` | ViewControl | Export view as PNG/JPG |
| `audit_model_standards` | BIM | Model health score 0-100, grade A-F |
| `find_duplicate_elements` | BIM | Find overlapping elements at same location |
| `audit_room_enclosure` | BIM | Check room enclosure, duplicates, not-placed |

### Infrastructure Improvements

- **Edit Mode Guard** — Safely detects Revit edit mode via reflection, returns friendly error
- **Empty Tag Detection** — `IndependentTag.HasTagText()` API (Revit 2025.3)
- **Context Auto-Injection** — View, selection, document info automatically injected with 30s cache
- **Tool Result Caching** — Thread-safe ConcurrentDictionary, 60s TTL, auto-invalidate on writes
- **Smart Error Context** — Enriches error messages with actionable suggestions

### Tool Enhancements

- `get_elements`: new `group_by` parameter (level/family/type/system)
- `get_untagged_elements`: new `system_name`, `level`, `include_empty_tags` filters

### Chat Engine

- **Multi-tool**: LLM can output up to 3 parallel `<tool_call>` blocks
- **Context Window**: Better token estimation (`/4`), structured summarization
- **Fallback**: Context-aware suggestions with relevant tool hints
- **Chitchat**: Expanded capability descriptions (170+ operations)

### Performance Optimization

| Optimization | Impact |
|-------------|--------|
| System prompt reduced ~40% | -1-3s prompt processing |
| Tool catalog: 25 max, compact format | -500-1500 tokens |
| Context collection: smart caching | -300-1500ms per message |
| Streaming: O(n) tool detection | -100-300ms |
| Two-stage: stricter trigger | Avoids 2x latency |
| System prompt + few-shot caching | Near-zero for repeated topics |
| ExtractToolCalls: pre-filter | -50-200ms |

**Estimated total improvement: 30-60% faster response time**

### Config Updates

- `keyword_groups.json` — 14 new tools mapped to groups, Vietnamese keywords added
- `tool_schema_hints.json` — Schema hints for all new tools
- `fewshot_examples.json` — 14 bilingual examples (Vietnamese + English)

---

## Installation

1. Download `HD.extension-v2.1.0.zip`
2. Extract to your pyRevit extensions folder
3. Restart Revit 2025

## Requirements

- Revit 2025 (net8.0-windows)
- Ollama with qwen2.5:7b or compatible model
- pyRevit installed

---

## Roadmap Progress

23/95 items completed from the [UPGRADE-ROADMAP](docs/UPGRADE-ROADMAP.md).
Next priorities: MEP critical path analysis, undo support, confirmation dry-run, rich result cards.
