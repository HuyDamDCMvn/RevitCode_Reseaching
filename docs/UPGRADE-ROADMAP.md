# RevitChat Bot - Upgrade Roadmap

> **Created:** 2026-02-23
> **Updated:** 2026-02-23 — Phase 1 (A1+A2), B1-B5, D1, E+F implemented
> **Current State:** 34 skills | ~190 tools | 6 packs
> **Reference:** Revit API 2025.3 ([revitapidocs.com/2025.3](https://www.revitapidocs.com/2025.3/news))

---

## Table of Contents

- [Phase 1: Quick Wins](#phase-1-quick-wins-infrastructure--tool-improvements)
- [Phase 2: New Core & View Tools](#phase-2-new-core--view-tools)
- [Phase 3: MEP Enhancement](#phase-3-mep-enhancement)
- [Phase 4: Chat Engine](#phase-4-chat-engine-improvements)
- [Phase 5: Modeler & BIM Coordinator](#phase-5-modeler--bim-coordinator)
- [Phase 6: New Skill Packs](#phase-6-new-skill-packs)
- [Phase 7: Data & Knowledge](#phase-7-data--knowledge)
- [Phase 8: Integration](#phase-8-integration)
- [Appendix: Revit API 2025.3 Features Used](#appendix-revit-api-20253-new-features-used)

---

## Phase 1: Quick Wins (Infrastructure & Tool Improvements)

**Estimated effort:** 1-2 weeks
**Goal:** Fix existing pain points, improve reliability and UX without adding new features.

### A. Infrastructure / UX

#### #1 — Edit Mode Guard
- **Priority:** P0 (Critical)
- **Files:** `RevitChat/Handler/RevitChatHandler.cs`
- **API:** `Document.IsInEditMode()` (Revit 2025.2)
- **Description:** Check if Revit is in an active edit mode (sketch, edit family, etc.) before executing any tool. Return a clear, user-friendly message instead of crashing with a generic transaction error.
- **Implementation:**
  1. In `RevitChatHandler.Execute()`, add guard at the top:
     ```csharp
     var doc = app.ActiveUIDocument?.Document;
     if (doc != null && doc.IsInEditMode())
     {
         // Drain queue and return friendly error for all pending requests
         while (_queue.TryDequeue(out var req))
             results[req.ToolCallId] = JsonSerializer.Serialize(new {
                 error = "Revit is in edit mode. Please finish or cancel the current edit operation first."
             });
         OnToolCallsCompleted?.Invoke(results);
         return;
     }
     ```
  2. Add Vietnamese translation in chat service system prompt.
- **Tests:** Manually enter sketch mode → send chat command → verify friendly message instead of crash.
- **Status:** `[x]` Completed (2026-02-23)

#### #2 — Empty Tag Detection
- **Priority:** P0 (Critical)
- **Files:** `SmartTag/Services/ElementCollector.cs`, `RevitChat/Skills/DimensionTagSkill.cs`
- **API:** `IndependentTag.HasTagText()` (Revit 2025.3)
- **Description:** Differentiate between tags with valid text vs empty/blank tags. Currently, elements with empty tags are counted as "tagged" even though they display no information.
- **Implementation:**
  1. `ElementCollector.EnsureTagCacheInitialized()`:
     - Add `_emptyTagCache` (HashSet<long>) alongside `_taggedElementCache`.
     - For each tag, check `tag.HasTagText()`. If false, add to `_emptyTagCache`.
  2. `ElementCollector.CheckExistingTag()`:
     - Return additional field `HasValidTagText`.
  3. `DimensionTagSkill.CollectTaggedElementIds()`:
     - Add `includeEmptyTags` parameter (default: false).
     - When false, skip tags where `!tag.HasTagText()`.
  4. `DimensionTagSkill.GetUntaggedElements()`:
     - Add `include_empty_tags` tool parameter.
     - Report empty-tag elements separately in response.
- **Tests:** Create elements with empty tags in test model → verify bot correctly identifies them as "untagged" or "empty-tagged".
- **Status:** `[x]` Completed (2026-02-23)

#### #3 — Undo/Rollback Tool
- **Priority:** P1
- **Files:** `RevitChat/Handler/RevitChatHandler.cs`, `RevitChat/Skills/ModifySkill.cs`
- **Description:** Allow the bot to undo its last transaction. Store transaction names in a stack; expose `undo_last_action` tool.
- **Implementation:**
  1. In `RevitChatHandler`, maintain a `Stack<string>` of transaction names created by the bot.
  2. Add `undo_last_action` tool to `ModifySkill`:
     - Call `doc.GetUndoStack()` → find last bot-created transaction → `doc.Undo()`.
     - Return what was undone.
  3. Limit to 1 undo (no redo chain).
- **Status:** `[ ]` Not started

#### #4 — Confirmation Summary (Auto Dry-Run)
- **Priority:** P1
- **Files:** `RevitChatLocal/Services/OllamaChatService.cs` (system prompt), `RevitChat/ViewModel/BaseChatViewModel.cs`
- **Description:** For destructive tools (delete, modify, tag_all, purge), the bot should automatically do a `dry_run=true` first, show summary to user, then execute with `dry_run=false` only after user confirms.
- **Implementation:**
  1. Define `DestructiveTools` set: `delete_elements`, `tag_all_in_view`, `purge_unused_elements`, `batch_update_parameters`, `set_parameter_value`, etc.
  2. In `BaseChatViewModel.ProcessToolCallLoopAsync()`, intercept destructive tool calls:
     - Force `dry_run=true` on first call.
     - Show preview to user with confirm/cancel buttons.
     - On confirm, re-send with `dry_run=false`.
  3. Update system prompt rule #7 to enforce this pattern.
- **Status:** `[ ]` Not started

#### #5 — Progress Callback
- **Priority:** P2
- **Files:** `RevitChat/Services/ToolExecutionService.cs`, `RevitChat/Handler/RevitChatHandler.cs`
- **Description:** For long-running tools (tag_all 500+ elements, clash_check), stream progress percentage back to UI.
- **Implementation:**
  1. Add `IProgress<string>` parameter to `BaseRevitSkill.ExecuteTool()`.
  2. In `RevitChatHandler.Execute()`, pass progress callback that fires `OnProgressUpdate` event.
  3. In `BaseChatViewModel`, listen to progress events and update status message.
  4. Skills can optionally report progress: `progress?.Report($"Processing {i}/{total}...")`.
- **Status:** `[ ]` Not started

#### #6 — Context Auto-Injection
- **Priority:** P1
- **Files:** `RevitChat/Services/ContextCollectionService.cs`, `RevitChatLocal/Services/OllamaChatService.cs`
- **Description:** Automatically inject current context (active view name/type, selected element IDs + categories, current level) into each chat message so the bot doesn't need to query for basic info.
- **Implementation:**
  1. Expand `ContextCollectionService` to collect:
     - Active view: name, type, level
     - Selection: count, category breakdown, first 10 element IDs
     - Document: filename, is workshared
  2. In `OllamaChatService.SendMessageAsync()`, prepend context block to user message:
     ```
     [Context] View: "Level 1 - MEP" (FloorPlan), Selection: 5 Pipes, Level: Level 1
     User: resize these pipes to DN100
     ```
  3. Update system prompt to acknowledge context block.
- **Status:** `[x]` Completed (2026-02-23)

#### #7 — Tool Result Caching
- **Priority:** P2
- **Files:** `RevitChat/Services/ToolExecutionService.cs`
- **Description:** Cache results of read-only tools (`get_elements`, `get_levels`, `count_elements`) within a conversation session. Invalidate on document change or modify tool execution.
- **Implementation:**
  1. Add `Dictionary<string, (DateTime, string)> _resultCache` to `ToolExecutionService`.
  2. Define `ReadOnlyTools` set.
  3. Before executing a read-only tool, check cache (key = functionName + serialized args).
  4. On any modify tool execution, clear entire cache.
  5. TTL: 60 seconds.
- **Status:** `[x]` Completed (2026-02-23)

#### #8 — Smart Retry with Context
- **Priority:** P2
- **Files:** `RevitChat/ViewModel/BaseChatViewModel.cs`
- **Description:** When a tool fails, inject error context + suggested fix into conversation history instead of generic error message.
- **Implementation:**
  1. In `ProcessToolCallLoopAsync()`, when tool returns `{ "error": "..." }`:
     - Parse error message.
     - Map common errors to suggestions:
       - "Element not found" → "Some element IDs may be invalid. Try `get_elements` first."
       - "Parameter is read-only" → "This parameter cannot be modified. Check parameter type."
       - "Transaction cannot start" → "Revit may be in edit mode."
     - Inject suggestion into `ContinueWithToolResultsAsync()` context.
- **Status:** `[x]` Completed (2026-02-23)

### B. Existing Tool Improvements

#### #9 — get_elements with Grouping
- **Priority:** P1
- **Files:** `RevitChat/Skills/QuerySkill.cs`
- **Description:** Add `group_by` parameter to `get_elements` (options: level, family, type, system). Return grouped results instead of flat list.
- **Implementation:**
  1. Add `group_by` param to tool definition (optional, string, enum: level|family|type|system).
  2. After collecting elements, group by specified field.
  3. Return `{ groups: [{ key: "Level 1", count: 25, elements: [...] }, ...] }`.
- **Status:** `[x]` Completed (2026-02-23)

#### #10 — export_to_csv Multi-Format
- **Priority:** P2
- **Files:** `RevitChat/Skills/ExportSkill.cs`
- **Description:** Support additional export formats: Excel (.xlsx), JSON, and formatted text table.
- **Implementation:**
  1. Add `format` param: csv (default), json, xlsx, txt.
  2. For xlsx: use ClosedXML or EPPlus library (add NuGet package).
  3. For json: structured JSON output.
  4. For txt: ASCII table format.
- **Dependencies:** ClosedXML NuGet package (for xlsx).
- **Status:** `[ ]` Not started

#### #11 — tag_all_in_view with SmartTag Algorithm
- **Priority:** P1
- **Files:** `RevitChat/Skills/DimensionTagSkill.cs`
- **Description:** Integrate SmartTag's placement algorithm (scoring, collision avoidance) into `tag_all_in_view` instead of placing tags at element centroids.
- **Implementation:**
  1. Reference SmartTag's `TagPlacementService` and `SpatialIndex`.
  2. When `tag_all_in_view` is called:
     - Collect elements via `ElementCollector`.
     - Calculate placements via `TagPlacementService.CalculatePlacements()`.
     - Resolve collisions via `TagPlacementService.ResolveCollisions()`.
     - Create tags at optimized positions.
  3. Add `use_smart_placement` param (default: true).
- **Status:** `[ ]` Not started

#### #12 — check_clashes Performance
- **Priority:** P2
- **Files:** `RevitChat/Skills/ClashDetectionSkill.cs`
- **Description:** Add spatial indexing for faster clash detection, system pair filtering, and progressive results.
- **Implementation:**
  1. Build BVH (bounding volume hierarchy) from element bounding boxes.
  2. Add `system_pair` filter param (e.g. "Pipes vs Ducts").
  3. Add `max_results` param with progressive reporting.
  4. Return estimated total even when capped.
- **Status:** `[ ]` Not started

#### #13 — get_untagged_elements Enhanced
- **Priority:** P1
- **Files:** `RevitChat/Skills/DimensionTagSkill.cs`
- **Description:** Add filters: by system name, by level, and `include_empty_tags` option.
- **Implementation:**
  1. Add params: `system_name`, `level`, `include_empty_tags`.
  2. Use `HasTagText()` API for empty tag detection.
  3. Report separately: `untagged_count`, `empty_tag_count`.
- **Status:** `[x]` Completed (2026-02-23)

#### #14 — batch_update_parameters Validation
- **Priority:** P2
- **Files:** `RevitChat/Skills/ModifySkill.cs`
- **Description:** Pre-validate parameter type compatibility and value range before committing changes.
- **Implementation:**
  1. Before transaction, check:
     - Parameter exists on all target elements.
     - Parameter storage type matches value type.
     - Value is within valid range (for numeric params).
  2. Return validation errors before asking for confirmation.
- **Status:** `[ ]` Not started

---

## Phase 2: New Core & View Tools

**Estimated effort:** 2-3 weeks
**Goal:** Expand core query/modify capabilities and view control tools.

### Core Pack (Query, Modify, Export)

#### #15 — get_element_geometry
- **Priority:** P2
- **Skill:** QuerySkill
- **Description:** Get geometry summary of an element: volume (m3), surface area (m2), length (m), bounding box dimensions.
- **Implementation:**
  1. Use `element.get_Geometry(Options)` → iterate `GeometryObject`.
  2. For solids: compute volume, surface area.
  3. For curves: compute length.
  4. Return bounding box min/max in mm.
- **Params:** `element_ids` (array), `include_faces` (bool, optional)
- **Status:** `[x]` Completed (2026-02-23)

#### #16 — find_elements_near
- **Priority:** P2
- **Skill:** QuerySkill
- **Description:** Find elements within a specified radius from a reference element or point.
- **Implementation:**
  1. Get reference element centroid (or use provided XYZ).
  2. Use `BoundingBoxIntersectsFilter` with expanded bounding box.
  3. Post-filter by actual distance.
  4. Return sorted by distance.
- **Params:** `reference_element_id` (int), `radius_mm` (number), `category` (optional), `limit` (optional)
- **Status:** `[x]` Completed (2026-02-23)

#### #17 — get_element_host
- **Priority:** P2
- **Skill:** QuerySkill
- **Description:** Get the host element of a hosted element (e.g., door's wall, fixture's floor).
- **Implementation:**
  1. Check `FamilyInstance.Host`.
  2. For MEP: check connectors for connected elements.
  3. Return host element info (id, category, name, type).
- **Params:** `element_ids` (array)
- **Status:** `[ ]` Not started

#### #18 — get_element_connections
- **Priority:** P2
- **Skill:** QuerySkill
- **Description:** Get all elements connected to a given element (shared faces, connectors, joins).
- **Implementation:**
  1. For walls: `WallUtils.GetWallsJoinedAtEnd()`.
  2. For MEP: iterate `ConnectorManager.Connectors`, get `AllRefs`.
  3. For structural: `AnalyticalModel` connections.
  4. Return connection graph.
- **Params:** `element_id` (int), `connection_type` (optional: all|connector|join|host)
- **Status:** `[ ]` Not started

#### #19 — undo_last_action
- **Priority:** P1
- **Skill:** ModifySkill
- **Description:** See #3 above.
- **Status:** `[ ]` Not started

#### #20 — create_element
- **Priority:** P3
- **Skill:** ModifySkill
- **Description:** Create basic elements (wall, floor, ceiling) from parameters.
- **Implementation:**
  1. `Wall.Create(doc, curve, typeId, levelId, height, offset, flip, structural)`.
  2. `Floor.Create(doc, curveLoop, floorTypeId, levelId)`.
  3. Start with walls only, expand later.
- **Params:** `element_type` (wall|floor), `points` (array of XYZ in mm), `type_id`, `level`, `height_mm`
- **Status:** `[ ]` Not started

#### #21 — export_to_excel
- **Priority:** P2
- **Skill:** ExportSkill
- **Description:** Export query results directly to .xlsx with auto-formatting, headers, and filters.
- **Dependencies:** ClosedXML NuGet package.
- **Params:** `data` (from previous tool result), `filename`, `sheet_name`
- **Status:** `[ ]` Not started

#### #22 — export_to_json
- **Priority:** P3
- **Skill:** ExportSkill
- **Description:** Export structured JSON for external tools/databases.
- **Params:** `data`, `filename`, `pretty_print`
- **Status:** `[ ]` Not started

#### #23 — import_from_csv
- **Priority:** P2
- **Skill:** ExportSkill
- **Description:** Import CSV data and batch-update element parameters. Map CSV columns to Revit parameters.
- **Implementation:**
  1. Parse CSV file (user provides path).
  2. Map columns: first column = Element ID, remaining = parameter names.
  3. Validate all mappings before committing.
  4. Use dry_run pattern.
- **Params:** `file_path`, `id_column`, `mappings` (optional), `dry_run`
- **Status:** `[ ]` Not started

### ViewControl Pack

#### #24 — create_callout
- **Priority:** P2
- **Skill:** ViewControlSkill
- **Description:** Create a callout view from a bounding box on a floor plan.
- **Params:** `parent_view_id`, `min_x`, `min_y`, `max_x`, `max_y` (in feet), `view_name`
- **Status:** `[ ]` Not started

#### #25 — create_drafting_view
- **Priority:** P3
- **Skill:** ViewControlSkill
- **Description:** Create a new drafting view with specified scale.
- **Params:** `name`, `scale` (e.g. 50 = 1:50)
- **Status:** `[ ]` Not started

#### #26 — set_view_range
- **Priority:** P2
- **Skill:** ViewControlSkill
- **Description:** Set view range (cut plane, top, bottom, depth) for plan views.
- **Params:** `view_id` (optional, default active), `cut_plane_mm`, `top_mm`, `bottom_mm`, `depth_mm`
- **Status:** `[x]` Completed (2026-02-23)

#### #27 — get_view_crop_region
- **Priority:** P3
- **Skill:** ViewControlSkill
- **Description:** Get/set crop region of a view.
- **Params:** `view_id`, `action` (get|set), `min_x`, `min_y`, `max_x`, `max_y` (for set)
- **Status:** `[ ]` Not started

#### #28 — override_color_by_system
- **Priority:** P1
- **Skill:** ViewControlSkill
- **Description:** Color-code MEP elements by system name. Auto-assign distinct colors per system.
- **Implementation:**
  1. Collect all MEP elements in view.
  2. Group by system name.
  3. Auto-assign colors from a predefined palette.
  4. Apply `OverrideGraphicSettings` per group.
- **Params:** `category` (Pipes|Ducts|all), `custom_colors` (optional dict: system_name → color)
- **Status:** `[x]` Completed (2026-02-23)

#### #29 — override_color_by_parameter
- **Priority:** P2
- **Skill:** ViewControlSkill
- **Description:** Heat map visualization: color elements by parameter value range.
- **Implementation:**
  1. Collect parameter values for all elements.
  2. Compute min/max range.
  3. Map values to gradient (blue → green → yellow → red).
  4. Apply overrides.
- **Params:** `category`, `param_name`, `color_low` (optional), `color_high` (optional)
- **Status:** `[x]` Completed (2026-02-23)

#### #30 — compare_views
- **Priority:** P3
- **Skill:** ViewControlSkill
- **Description:** Compare two views: list elements that differ in visibility, overrides, or existence.
- **Params:** `view_id_1`, `view_id_2`
- **Status:** `[ ]` Not started

#### #31 — screenshot_view
- **Priority:** P2
- **Skill:** ViewControlSkill
- **Description:** Export current view as image (PNG/JPG).
- **Implementation:**
  1. Use `ImageExportOptions` + `doc.ExportImage()`.
  2. Save to temp folder, return file path.
- **Params:** `view_id` (optional), `format` (png|jpg), `resolution` (low|medium|high)
- **Status:** `[x]` Completed (2026-02-23)

---

## Phase 3: MEP Enhancement

**Estimated effort:** 3-4 weeks
**Goal:** Leverage new Revit 2025 Analysis APIs for MEP system analysis, add advanced validation and modeling tools.

### MEP System Analysis

#### #32 — get_critical_path
- **Priority:** P1
- **API:** `CriticalPathCollector` (Revit 2025)
- **Skill:** MepSystemAnalysisSkill
- **Description:** Get the critical path of a duct/pipe network with flow rate and total pressure drop.
- **Implementation:**
  1. Find MEP system by name or selected elements.
  2. Get analytical network from system.
  3. Use `CriticalPathCollector` to get critical path data.
  4. Return: total length, total pressure drop, flow rate, segment count.
- **Params:** `system_name`, `include_segments` (bool)
- **Status:** `[ ]` Not started

#### #33 — analyze_pressure_loss
- **Priority:** P1
- **API:** `CriticalPathIterator` (Revit 2025)
- **Skill:** MepSystemAnalysisSkill
- **Description:** Per-segment pressure loss analysis along the critical path.
- **Implementation:**
  1. Use `CriticalPathIterator` to traverse each segment.
  2. For each segment: element ID, type, size, length, pressure drop, cumulative drop.
  3. Identify the segment with highest pressure loss.
- **Params:** `system_name`, `sort_by` (pressure_drop|length|cumulative)
- **Status:** `[ ]` Not started

#### #34 — traverse_mep_network
- **Priority:** P2
- **API:** `MEPNetworkIterator(Document, MEPAnalyticalSegment)` (Revit 2025)
- **Skill:** MepConnectivitySkill
- **Description:** Traverse the full MEP analytical network from a given segment, covering both sides.
- **Params:** `element_id` (starting element), `direction` (both|upstream|downstream), `max_depth`
- **Status:** `[ ]` Not started

#### #35 — get_flow_distribution
- **Priority:** P2
- **Skill:** MepSystemAnalysisSkill
- **Description:** Get flow rate distribution for all branches in a network.
- **Params:** `system_name`, `unit` (L/s|CFM|m3/h)
- **Status:** `[ ]` Not started

### MEP Validation (New Tools)

#### #36 — check_velocity
- **Priority:** P1
- **Skill:** MepValidationSkill
- **Description:** Check flow velocity in pipes/ducts against design limits. Flag elements exceeding maximum velocity.
- **Implementation:**
  1. Read `RBS_PIPE_FLOW_PARAM` or `RBS_DUCT_FLOW_PARAM`.
  2. Calculate velocity from flow / cross-section area.
  3. Compare against limits (configurable: default 2.5 m/s pipe, 8 m/s duct).
- **Params:** `category` (Pipes|Ducts), `max_velocity_ms` (optional), `system_name` (optional)
- **Status:** `[x]` Completed (2026-02-23)

#### #37 — check_noise_level
- **Priority:** P3
- **Skill:** MepValidationSkill
- **Description:** Estimate noise level from velocity + duct/pipe size using standard formulas.
- **Params:** `category`, `system_name`, `max_db` (optional)
- **Status:** `[ ]` Not started

#### #38 — auto_size_mep
- **Priority:** P2
- **Skill:** MepModelerSkill
- **Description:** Auto-size pipes/ducts based on flow rate and target velocity.
- **Implementation:**
  1. Read flow from analytical data.
  2. Calculate required diameter: D = sqrt(4Q / (π × v)).
  3. Round to nearest standard size.
  4. Apply via `resize_mep_elements` logic.
- **Params:** `element_ids` (array), `target_velocity_ms`, `dry_run`
- **Status:** `[ ]` Not started

#### #39 — route_mep_between
- **Priority:** P3
- **Skill:** MepModelerSkill
- **Description:** Auto-route pipe/duct between two connector points with obstacle avoidance.
- **Params:** `start_element_id`, `end_element_id`, `mep_type` (Pipe|Duct), `type_name`, `elevation_mm`
- **Note:** Complex feature, may require multiple iterations.
- **Status:** `[ ]` Not started

#### #40 — get_mep_elevation_table
- **Priority:** P1
- **Skill:** MepSystemAnalysisSkill
- **Description:** Generate elevation summary table for all MEP elements grouped by system and level.
- **Params:** `categories` (array, optional), `group_by` (system|level|both)
- **Status:** `[ ]` Not started

#### #41 — check_insulation_coverage
- **Priority:** P1
- **Skill:** MepValidationSkill
- **Description:** Find pipes/ducts that should have insulation but don't.
- **Implementation:**
  1. Collect all pipes/ducts in view/model.
  2. Check `InsulationLiningBase.GetInsulationIds()` for each.
  3. Return uninsulated elements with system info.
- **Params:** `category` (Pipes|Ducts|all), `system_name` (optional), `level` (optional)
- **Status:** `[x]` Completed (2026-02-23)

#### #42 — check_access_panel
- **Priority:** P3
- **Skill:** MepValidationSkill
- **Description:** Check if valves/dampers are accessible (distance to floor/ceiling within limits).
- **Params:** `min_height_mm`, `max_height_mm`, `element_ids` (optional)
- **Status:** `[ ]` Not started

#### #43 — create_pipe_network
- **Priority:** P3
- **Skill:** MepModelerSkill
- **Description:** Create a basic pipe network from equipment to fixtures.
- **Note:** Advanced feature, recommend Phase 6+.
- **Status:** `[ ]` Not started

#### #44 — get_ceiling_grid
- **Priority:** P2
- **API:** `Ceiling.GetCeilingGridLine()` (Revit 2025.3)
- **Skill:** MepSystemAnalysisSkill (or new ArchitecturalSkill)
- **Description:** Get ceiling grid geometry for coordination with MEP elements.
- **Params:** `ceiling_id` (optional, default all in view), `include_boundary`
- **Status:** `[ ]` Not started

---

## Phase 4: Chat Engine Improvements

**Estimated effort:** 2-3 weeks
**Goal:** Improve LLM interaction quality, reduce latency, and add advanced conversation features.

#### #68 — Multi-tool Parallel Execution
- **Priority:** P2
- **Files:** `OllamaChatService.cs`, `BaseChatViewModel.cs`
- **Description:** Allow LLM to output multiple `<tool_call>` tags in one response. Execute all on the Revit thread in sequence (they share the same thread).
- **Implementation:**
  1. `ExtractToolCalls()` already returns `List<ToolCallRequest>` — just stop limiting to 1.
  2. Update system prompt: "You may output up to 3 <tool_call> blocks per response for independent operations."
  3. In `ProcessToolCallLoopAsync()`, batch-enqueue all tool calls.
- **Status:** `[x]` Completed (2026-02-23)

#### #69 — Planning Mode
- **Priority:** P2
- **Description:** For complex multi-step requests, LLM first outputs a plan (numbered steps), user approves, then executes step by step.
- **Implementation:**
  1. Detect complex requests (multiple action verbs, long prompts).
  2. First LLM call: generate plan (no tool calls).
  3. Show plan to user with "Execute" / "Modify" buttons.
  4. On approve: execute each step sequentially.
- **Status:** `[ ]` Not started

#### #70 — Working Memory (Persistent Tool Results)
- **Priority:** P1
- **Files:** `ToolExecutionService.cs`, `OllamaChatService.cs`
- **Description:** Persist key tool results across conversation turns in a structured memory. Avoid re-sending full JSON data in every `ContinueWithToolResultsAsync()`.
- **Implementation:**
  1. Add `WorkingMemory` class: stores last N tool results as key-value.
  2. In system prompt, reference memory: "You have access to previous results in [Memory]."
  3. Only send summary/diff instead of full data on continuation.
- **Status:** `[ ]` Not started

#### #71 — Streaming Tool Detection (Early Detection)
- **Priority:** P2
- **Files:** `OllamaChatService.cs`
- **Description:** Detect `<tool_call>` tag as early as possible during streaming. Stop showing tokens to user once tool call is detected. Currently checks after full response.
- **Implementation:**
  1. In `StreamCompletionAsync()`, check buffer for `<tool_call>` on every token.
  2. Once detected, stop emitting `TokenReceived` events.
  3. Continue collecting until `</tool_call>` or end of stream.
- **Note:** Current code already has basic detection. Improve reliability.
- **Status:** `[ ]` Not started

#### #72 — Conversation Branching
- **Priority:** P3
- **Description:** Allow user to "go back" to a previous message and retry. Conversation tree instead of linear history.
- **Implementation:**
  1. Store conversation as tree (each message has parent ID).
  2. UI: click on any previous message → fork from there.
  3. Previous branch preserved but hidden.
- **Status:** `[ ]` Not started

#### #73 — Context Window Optimization
- **Priority:** P1
- **Files:** `OllamaChatService.cs` → `TrimHistory()`
- **Description:** Replace current simple history trimming with structured summarization.
- **Implementation:**
  1. When history exceeds threshold:
     - Keep last 4 user/assistant pairs intact.
     - Summarize older messages: "User queried 15 pipes on Level 1, bot found 3 oversized."
     - Store summaries as system messages.
  2. Estimate tokens more accurately (current: `length/3`, improve with tiktoken-like counting).
- **Status:** `[x]` Completed (2026-02-23)

#### #74 — Model-Specific Prompts
- **Priority:** P2
- **Files:** `OllamaChatService.cs`, new config files
- **Description:** Optimize system prompt and tool_call format per LLM model.
- **Implementation:**
  1. Create `model_prompts/` config directory.
  2. Per model: `qwen2.5.json`, `llama3.json`, `mistral.json`.
  3. Each defines: tool_call format, special tokens, max tools in catalog, few-shot style.
  4. Load based on `SelectedModel`.
- **Status:** `[ ]` Not started

#### #75 — Feedback Loop Enhancement (Self-Improving)
- **Priority:** P2
- **Files:** `RevitChat/Services/ChatFeedbackService.cs`
- **Description:** After user approves/rejects a tool result, automatically update fewshot_examples.json with the approved pattern.
- **Implementation:**
  1. On user thumbs-up: save (userMessage, toolName, args) to approved_examples.json.
  2. On user thumbs-down: save to rejected_examples.json with correction.
  3. `BuildDynamicExamples()` already checks `ChatFeedbackService.GetSimilarApproved()` — extend this.
  4. Periodically merge approved examples into fewshot_examples.json.
- **Status:** `[ ]` Not started

#### #76 — RAG Integration
- **Priority:** P3
- **Description:** Index Revit API documentation + project BIM standards into a vector store. Inject relevant chunks into system prompt for better answers.
- **Implementation:**
  1. Pre-process Revit API docs into chunks.
  2. Use local embedding model (e.g., all-MiniLM-L6-v2) to create vectors.
  3. Store in simple flat-file vector index.
  4. On each query, find top-3 relevant chunks, inject into system prompt.
- **Dependencies:** ML.NET or ONNX runtime for embeddings.
- **Status:** `[ ]` Not started

#### #77 — Multi-Model Pipeline
- **Priority:** P3
- **Description:** Use small model (1-3B) for tool selection, large model (7B+) for response generation.
- **Implementation:**
  1. Stage 1: Send user message to small model with tool catalog → get tool name + args.
  2. Stage 2: Execute tool.
  3. Stage 3: Send tool results to large model → generate natural language response.
  4. This is similar to existing Two-Stage mode but with different models per stage.
- **Status:** `[ ]` Not started

### UI/UX Improvements

#### #78 — Rich Result Cards
- **Priority:** P2
- **Files:** `RevitChatLocal/Views/`, `BaseChatViewModel.cs`
- **Description:** Display tool results as formatted cards: tables, mini charts, color swatches.
- **Implementation:**
  1. Define `ChatMessageType` enum: Text, Table, Chart, ElementList.
  2. Parse tool result JSON → detect structure → render appropriate template.
  3. WPF DataTemplates for each type.
- **Status:** `[ ]` Not started

#### #79 — Quick Actions Bar
- **Priority:** P1
- **Description:** Row of buttons below chat input for frequent actions: "Count", "Warnings", "Color by System", "Export".
- **Implementation:**
  1. Add `ObservableCollection<QuickAction>` to ViewModel.
  2. Each QuickAction: icon, label, pre-filled prompt.
  3. On click: auto-send the prompt.
  4. Make configurable per user.
- **Status:** `[ ]` Not started

#### #80 — Template Prompts
- **Priority:** P2
- **Description:** Predefined prompt templates: "QC Check", "MEP Summary Report", "Tag All Pipes".
- **Implementation:**
  1. Create `prompt_templates.json` config file.
  2. UI: dropdown/popup with template list.
  3. Templates can have `{placeholders}` that user fills in.
- **Status:** `[ ]` Not started

#### #81 — History Search
- **Priority:** P3
- **Description:** Search within conversation history to find previous results.
- **Status:** `[ ]` Not started

#### #82 — Multi-language Toggle
- **Priority:** P2
- **Description:** Explicit language toggle (VI/EN/DE) instead of auto-detection.
- **Implementation:**
  1. Add language selector in settings.
  2. Inject `"Reply in {language}"` into system prompt.
  3. Translate UI labels.
- **Status:** `[ ]` Not started

#### #83 — Voice Input
- **Priority:** P3
- **Description:** Speech-to-text for hands-free operation.
- **Dependencies:** System.Speech or Azure Speech SDK.
- **Status:** `[ ]` Not started

#### #84 — Batch Command / Macro
- **Priority:** P3
- **Description:** User defines command sequences (macros): "QC Check = count_elements + get_model_warnings + audit_view_names". Save and replay.
- **Status:** `[ ]` Not started

---

## Phase 5: Modeler & BIM Coordinator

**Estimated effort:** 3-4 weeks
**Goal:** Expand modeling and BIM coordination capabilities.

### Modeler Pack (New Tools)

#### #45 — create_dimension
- **Priority:** P2
- **API:** `LinearDimension.Create()`, `RadialDimension.Create()`, `ArcLengthDimension.Create()` (Revit 2025)
- **Skill:** DimensionTagSkill
- **Description:** Create dimensions between references in the active view.
- **Params:** `dimension_type` (linear|radial|arc), `reference_ids` (array), `line_point` (XYZ)
- **Status:** `[ ]` Not started

#### #46 — create_spot_elevation
- **Priority:** P3
- **Skill:** DimensionTagSkill
- **Description:** Create spot elevation annotations on floor plans.
- **Params:** `element_id`, `face_reference`, `point` (XYZ)
- **Status:** `[ ]` Not started

#### #47 — align_tags
- **Priority:** P1
- **API:** `AnnotationMultipleAlignmentUtils` (Revit 2025)
- **Skill:** DimensionTagSkill
- **Description:** Align multiple tags/text notes to each other.
- **Implementation:**
  1. Use `AnnotationMultipleAlignmentUtils.MoveWithAnchoredLeaders()`.
  2. Use `GetAnnotationOutlineWithoutLeaders()` for bounds.
  3. Support alignment: left, right, center, top, bottom.
- **Params:** `tag_ids` (array), `alignment` (left|right|center|top|bottom), `reference_tag_id` (optional)
- **Status:** `[x]` Completed (2026-02-23)

#### #48 — get_empty_tags
- **Priority:** P1
- **API:** `IndependentTag.HasTagText()` (Revit 2025.3)
- **Skill:** DimensionTagSkill
- **Description:** Find all tags in the view that display empty/no text.
- **Params:** `category` (optional), `limit`
- **Returns:** List of empty tags with host element info.
- **Status:** `[x]` Completed (2026-02-23)

#### #49 — create_wall
- **Priority:** P3
- **Skill:** FamilyPlacementSkill
- **Description:** Create a wall from two points, wall type, level, and height.
- **Params:** `start_point_mm` (XY), `end_point_mm` (XY), `type_id`, `level`, `height_mm`
- **Status:** `[ ]` Not started

#### #50 — create_floor
- **Priority:** P3
- **Skill:** FamilyPlacementSkill
- **Description:** Create a floor from boundary points.
- **Params:** `boundary_points_mm` (array of XY), `floor_type_id`, `level`
- **Status:** `[ ]` Not started

#### #51 — split_wall
- **Priority:** P3
- **Skill:** ModifySkill
- **Description:** Split a wall at a specified point.
- **Params:** `wall_id`, `split_point_mm` (XY)
- **Status:** `[ ]` Not started

#### #52 — join_elements
- **Priority:** P2
- **Skill:** ModifySkill
- **Description:** Join/unjoin geometry between elements.
- **Params:** `element_id_1`, `element_id_2`, `action` (join|unjoin)
- **Status:** `[ ]` Not started

#### #53 — get_wall_layers
- **Priority:** P2
- **Skill:** QuerySkill
- **Description:** Get wall type compound structure: layers, materials, thickness.
- **Params:** `wall_id` or `wall_type_id`
- **Returns:** Array of layers with material name, thickness_mm, function (Structure, Finish, etc.)
- **Status:** `[x]` Completed (2026-02-23)

#### #54 — manage_sheet_collection
- **Priority:** P3
- **API:** `SheetCollection.Create()` (Revit 2025)
- **Skill:** SheetManagementSkill
- **Description:** Create and manage sheet collections (groups).
- **Params:** `action` (create|list|assign), `name`, `sheet_ids` (for assign)
- **Status:** `[ ]` Not started

#### #55 — import_step
- **Priority:** P3
- **API:** `STEPImportOptions`, `Document.Import()` (Revit 2025)
- **Skill:** FamilyPlacementSkill
- **Description:** Import a STEP file into the current document.
- **Params:** `file_path`, `view_id` (optional)
- **Status:** `[ ]` Not started

#### #56 — export_pdf_background
- **Priority:** P2
- **API:** `PDFExportOptions.SetExportInBackground()` (Revit 2025)
- **Skill:** ExportSkill
- **Description:** Export views/sheets to PDF in background (non-blocking).
- **Params:** `view_ids` (array), `output_folder`, `filename_pattern`
- **Status:** `[ ]` Not started

### BIM Coordinator Pack (New Tools)

#### #57 — audit_model_standards
- **Priority:** P1
- **Skill:** ModelHealthSkill
- **Description:** Comprehensive all-in-one audit: naming, families, parameters, warnings. Single report.
- **Implementation:**
  1. Combine: `audit_view_names` + `get_model_warnings` + `find_unused_families` + `check_parameter_values`.
  2. Score each category (0-100).
  3. Return overall "Model Health Score" + breakdown.
- **Params:** `include` (optional array: naming|warnings|families|parameters, default all)
- **Status:** `[x]` Completed (2026-02-23)

#### #58 — compare_model_versions
- **Priority:** P3
- **Skill:** CoordinationReportSkill
- **Description:** Compare current model with a saved version: elements added/removed/modified.
- **Note:** Requires saving element snapshots. Complex feature.
- **Status:** `[ ]` Not started

#### #59 — generate_qc_report
- **Priority:** P1
- **Skill:** CoordinationReportSkill
- **Description:** Generate a QC checklist report with pass/fail items, exportable to HTML or CSV.
- **Implementation:**
  1. Define QC checklist items (configurable JSON).
  2. Run each check → pass/fail/warning.
  3. Generate report with summary + details.
  4. Export to HTML (with CSS styling) or CSV.
- **Params:** `checklist` (optional, default all), `export_format` (html|csv|json)
- **Status:** `[ ]` Not started

#### #60 — check_link_coordinates
- **Priority:** P2
- **Skill:** RevitLinkSkill
- **Description:** Verify linked model shared coordinates alignment.
- **Implementation:**
  1. Get link transform: `RevitLinkInstance.GetTransform()`.
  2. Check if origin offset is within tolerance.
  3. Compare shared coordinates with host.
- **Params:** `link_id` (optional, default all), `tolerance_mm`
- **Status:** `[ ]` Not started

#### #61 — find_duplicate_elements
- **Priority:** P2
- **Skill:** PurgeAuditSkill
- **Description:** Find elements at the same location (overlapping walls, duplicate pipes).
- **Implementation:**
  1. Group elements by category + location (rounded to tolerance).
  2. Within each group, check for overlapping bounding boxes.
  3. For linear elements: check if curves are coincident.
- **Params:** `category`, `tolerance_mm` (default 10)
- **Status:** `[x]` Completed (2026-02-23)

#### #62 — audit_room_enclosure
- **Priority:** P2
- **Skill:** RoomAreaSkill
- **Description:** Check if rooms are properly bounded, find missing room separation lines.
- **Implementation:**
  1. Iterate rooms: check `Room.Area > 0` and `Room.Location != null`.
  2. Find "Not Enclosed" rooms.
  3. Suggest locations for room separation lines.
- **Params:** `level` (optional)
- **Status:** `[x]` Completed (2026-02-23)

---

## Phase 6: New Skill Packs

**Estimated effort:** 4+ weeks
**Goal:** Add entirely new capability domains.

### Electrical Pack

#### #63 — ElectricalAnalysisSkill
- **Priority:** P2
- **API:** `ElectricalPerPhaseData`, `AnalyticalTransformerData`, `AnalyticalPowerDistributableNodeData` (Revit 2025)
- **Tools:**
  - `get_panel_schedules` — List all electrical panels with load summaries.
  - `get_circuit_loads` — Get load breakdown per circuit.
  - `check_panel_capacity` — Check if any panel exceeds capacity.
  - `get_voltage_drop` — Calculate voltage drop for circuits.
  - `get_phase_balance` — Check phase balance across panels.
- **Status:** `[ ]` Not started

### Structure Pack

#### #64 — StructuralAnalysisSkill
- **Priority:** P3
- **API:** Rebar Splice API (Revit 2025), `AnalyticalElement.SetTransform()` (Revit 2025)
- **Tools:**
  - `get_structural_model` — Get structural elements summary (columns, beams, foundations).
  - `check_rebar_coverage` — Check rebar spacing and coverage requirements.
  - `get_rebar_schedule` — Generate rebar schedule/BOQ.
  - `check_foundation_loads` — Verify foundation sizing vs loads.
- **Status:** `[ ]` Not started

### Energy Pack

#### #65 — EnergyAnalysisSkill
- **Priority:** P3
- **API:** `BuildingOperatingYearSchedule`, `BuildingOperatingDaySchedule`, `GBXMLExportOptions` (Revit 2025)
- **Tools:**
  - `get_building_schedules` — List operating schedules.
  - `set_operating_schedule` — Create/modify operating schedules.
  - `export_gbxml` — Export model to gbXML for energy analysis.
  - `get_space_energy_data` — Get energy-related space data.
- **Status:** `[ ]` Not started

### Context Menu Integration

#### #66 — ContextMenuSkill
- **Priority:** P2
- **API:** `IContextMenuCreator`, `ContextMenu`, `CommandMenuItem` (Revit 2025)
- **Description:** Register right-click context menu items that trigger chat tools directly.
- **Implementation:**
  1. Create `AiContextMenuCreator : IContextMenuCreator`.
  2. Menu items:
     - "Ask AI about selection" → opens chat with selected elements as context.
     - "AI: Tag selected" → directly calls `tag_elements`.
     - "AI: Color by system" → calls `override_color_by_system`.
     - "AI: Check issues" → calls validation tools.
  3. Register in `Entry.cs`: `uiApp.RegisterContextMenu()`.
- **Files to create:** `RevitChat/UI/AiContextMenuCreator.cs`, update `Entry.cs`
- **Status:** `[ ]` Not started

### Add-in Management

#### #67 — AddInManagementSkill
- **Priority:** P3
- **API:** `AddInsManagerSettings`, `AddInItemSettings` (Revit 2025.3)
- **Tools:**
  - `get_loaded_addins` — List all registered add-ins with load times.
  - `disable_addin` — Disable a specific add-in for next session.
  - `get_addin_load_times` — Diagnose slow-loading add-ins.
- **Status:** `[ ]` Not started

---

## Phase 7: Data & Knowledge

**Estimated effort:** Ongoing

#### #85 — Company Standards Library
- **Priority:** P2
- **Description:** Load company BIM standards (naming, parameters, families) as configurable rules. Bot enforces during modify operations.
- **Implementation:**
  1. Create `company_standards.json` config:
     - Naming patterns per category.
     - Required parameters per category.
     - Forbidden families list.
  2. `ChatGuardService` validates tool calls against standards before execution.
- **Status:** `[ ]` Not started

#### #86 — Project-Specific Training
- **Priority:** P2
- **Description:** Per-project fewshot examples. Bot learns from feedback history of each project.
- **Implementation:**
  1. Store approved examples per project (keyed by document GUID).
  2. On new project: start with global examples.
  3. As user provides feedback: build project-specific example set.
- **Status:** `[ ]` Not started

#### #87 — Revit API Knowledge Base
- **Priority:** P3
- **Description:** Index Revit API documentation for bot to answer "how to" questions.
- **Status:** `[ ]` Not started

#### #88 — Cross-Project Analytics
- **Priority:** P3
- **Description:** Compare model health metrics across projects. Trend analysis.
- **Status:** `[ ]` Not started

#### #89 — Knowledge Graph
- **Priority:** P3
- **Description:** Build element relationship graph for complex queries: "All pipes from pump B-01 to floor 5".
- **Status:** `[ ]` Not started

---

## Phase 8: Integration

**Estimated effort:** Long-term

#### #90 — MCP Server (Model Context Protocol)
- **Priority:** P1
- **Description:** Implement MCP server for Revit. External AI tools (Claude, Cursor, etc.) can call Revit tools.
- **Reference:** See `docs/MCP-ARCHITECTURE-PLAN.md` for existing plan.
- **Status:** `[ ]` Not started

#### #91 — Webhook/REST API
- **Priority:** P3
- **Description:** REST API endpoint for external systems to call bot tools.
- **Status:** `[ ]` Not started

#### #92 — SmartTag ↔ Chat Sync
- **Priority:** P2
- **Description:** Sync SmartTag results (tagged elements, placements) into chat context. Bot knows what SmartTag has done.
- **Implementation:**
  1. SmartTag writes results to shared state (file or in-memory).
  2. Chat reads SmartTag state when answering tag-related queries.
- **Status:** `[ ]` Not started

#### #93 — Navisworks Export
- **Priority:** P3
- **Description:** Export clash results to Navisworks format (.nwc/.bcf).
- **Status:** `[ ]` Not started

#### #94 — IFC Integration
- **Priority:** P2
- **API:** `IFCCategoryTemplate`, `ExportIFCCategoryInfo` (Revit 2025)
- **Description:** Manage IFC export category mappings from chat.
- **Tools:**
  - `get_ifc_mappings` — List current IFC category mappings.
  - `set_ifc_mapping` — Modify mapping for a category.
  - `export_ifc` — Export model to IFC with current mappings.
- **Status:** `[ ]` Not started

#### #95 — Dynamo Bridge
- **Priority:** P3
- **Description:** Execute Dynamo scripts from chat. User: "run pipe routing script" → bot triggers Dynamo graph.
- **Status:** `[ ]` Not started

---

## Appendix: Revit API 2025.3 New Features Used

| API Feature | Item(s) | Version |
|-------------|---------|---------|
| `Document.IsInEditMode()` | #1 | 2025.2 |
| `IndependentTag.HasTagText()` | #2, #13, #48 | 2025.3 |
| `Ceiling.GetCeilingGridLine()` | #44 | 2025.3 |
| `SpatialElement.Recenter()` / `GetDefaultLocation()` | #62 (related) | 2025.3 |
| `AddInsManagerSettings` / `AddInItemSettings` | #67 | 2025.3 |
| `Wall.AddAttachment()` / `RemoveAttachment()` | Future wall tools | 2025.2 |
| `CriticalPathCollector` / `CriticalPathIterator` | #32, #33 | 2025 |
| `MEPNetworkIterator(Document, MEPAnalyticalSegment)` | #34 | 2025 |
| `LinearDimension.Create()` / `RadialDimension.Create()` | #45 | 2025 |
| `AnnotationMultipleAlignmentUtils` | #47 | 2025 |
| `ElectricalPerPhaseData` / `AnalyticalTransformerData` | #63 | 2025 |
| `BuildingOperatingYearSchedule` / `DaySchedule` | #65 | 2025 |
| `GBXMLExportOptions` (conceptual mass) | #65 | 2025 |
| `IContextMenuCreator` / `ContextMenu` | #66 | 2025 |
| `SheetCollection.Create()` | #54 | 2025 |
| `STEPImportOptions` / `Document.Import()` | #55 | 2025 |
| `PDFExportOptions.SetExportInBackground()` | #56 | 2025 |
| `IFCCategoryTemplate` / `ExportIFCCategoryInfo` | #94 | 2025 |
| `RebarSplice` / `RebarSpliceRules` | #64 | 2025 |
| `DuctSettings.AirDynamicViscosity` | #33 (related) | 2025 |

---

## Priority Legend

| Priority | Meaning |
|----------|---------|
| **P0** | Critical — fix existing bugs / UX issues |
| **P1** | High — significant user value, moderate effort |
| **P2** | Medium — good value, plan for next sprint |
| **P3** | Low — nice to have, long-term backlog |

## Status Legend

| Status | Meaning |
|--------|---------|
| `[ ]` | Not started |
| `[~]` | In progress |
| `[x]` | Completed |
| `[-]` | Cancelled / Deferred |

---

## Summary

| Phase | Items | Priority Mix | Est. Effort |
|-------|-------|-------------|-------------|
| Phase 1: Quick Wins | #1–#14 (14 items) | 2×P0, 5×P1, 7×P2 | 1-2 weeks |
| Phase 2: Core & View | #15–#31 (17 items) | 2×P1, 9×P2, 6×P3 | 2-3 weeks |
| Phase 3: MEP | #32–#44 (13 items) | 4×P1, 4×P2, 5×P3 | 3-4 weeks |
| Phase 4: Chat Engine | #68–#84 (17 items) | 3×P1, 8×P2, 6×P3 | 2-3 weeks |
| Phase 5: Modeler & BIM | #45–#62 (18 items) | 3×P1, 7×P2, 8×P3 | 3-4 weeks |
| Phase 6: New Packs | #63–#67 (5 items) | 1×P2, 4×P3 | 4+ weeks |
| Phase 7: Data | #85–#89 (5 items) | 2×P2, 3×P3 | Ongoing |
| Phase 8: Integration | #90–#95 (6 items) | 1×P1, 2×P2, 3×P3 | Long-term |
| **Total** | **95 items** | **14×P0-P1, 42×P2, 39×P3** | — |
