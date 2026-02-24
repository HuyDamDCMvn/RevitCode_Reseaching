using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RevitChat.Models;

namespace RevitChat.Services
{
    /// <summary>
    /// Patches missing tool call arguments using entities detected by PromptAnalyzer.
    /// Runs after LLM returns tool calls but before execution.
    /// Only injects when the argument is absent — never overrides LLM's explicit choices.
    /// </summary>
    public static class ToolCallEnricher
    {
        private static readonly HashSet<string> LevelAwareTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "count_elements", "get_elements", "search_elements",
            "export_to_csv", "export_to_json",
            "get_duct_summary", "get_pipe_summary", "get_conduit_summary", "get_cable_tray_summary",
            "calculate_system_totals",
            "mep_quantity_takeoff",
            "check_velocity", "check_pipe_slope", "check_disconnected_elements",
            "check_insulation_coverage", "check_oversized", "check_missing_params",
            "check_fire_dampers", "check_slope_continuity",
            "get_mechanical_equipment", "get_electrical_equipment",
            "get_plumbing_fixtures", "get_fire_protection_equipment", "get_fittings",
            "get_rooms", "get_rooms_detailed",
            "get_untagged_elements", "tag_all_in_view",
            "override_category_color", "isolate_category",
            "select_elements", "zoom_to_elements",
        };

        private static readonly HashSet<string> SystemAwareTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "get_duct_summary", "get_pipe_summary", "get_conduit_summary",
            "calculate_system_totals", "get_system_elements",
            "check_velocity", "check_pipe_slope", "check_disconnected_elements",
            "check_insulation_coverage",
            "override_color_by_system",
            "mep_quantity_takeoff",
        };

        private static readonly HashSet<string> CategoryAwareTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "count_elements", "get_elements", "search_elements",
            "export_to_csv", "export_to_json",
            "get_untagged_elements", "tag_all_in_view",
            "override_category_color", "isolate_category", "hide_category",
        };

        // B1: tools that accept dry_run parameter
        private static readonly HashSet<string> DryRunAwareTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "set_parameter_value", "delete_elements", "rename_elements",
            "copy_elements", "move_elements", "mirror_elements",
            "resize_mep_elements", "split_mep_elements", "set_pipe_slope",
            "batch_set_offset", "join_geometry", "unjoin_geometry",
            "add_change_insulation", "connect_mep_elements",
            "create_element", "create_duct", "create_pipe",
            "auto_size_mep", "route_mep_between",
        };

        // B3: tools that accept limit parameter
        private static readonly HashSet<string> LimitAwareTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "get_elements", "search_elements", "count_elements",
            "export_to_csv", "export_to_json",
            "get_duct_summary", "get_pipe_summary",
            "get_rooms", "get_rooms_detailed",
            "get_mechanical_equipment", "get_electrical_equipment",
            "get_plumbing_fixtures", "get_untagged_elements",
            "get_model_warnings", "check_clashes",
        };

        // B4: tools that accept element_ids parameter
        private static readonly HashSet<string> ElementIdAwareTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "set_parameter_value", "delete_elements", "rename_elements",
            "copy_elements", "move_elements", "mirror_elements",
            "resize_mep_elements", "split_mep_elements",
            "connect_mep_elements", "tag_elements",
            "override_element_color", "select_elements", "zoom_to_elements",
            "get_element_geometry", "batch_set_offset",
            "join_geometry", "unjoin_geometry",
            "route_mep_between",
        };

        // B2: mapping from (tool_name) -> list of (unit, param_name) for numeric injection
        private static readonly Dictionary<string, (string unit, string paramName)[]> NumericParamMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["resize_mep_elements"] = new[] { ("mm", "diameter_mm"), ("mm_w", "width_mm"), ("mm_h", "height_mm") },
            ["split_mep_elements"] = new[] { ("mm", "segment_length_mm") },
            ["set_pipe_slope"] = new[] { ("%", "slope_pct") },
            ["check_velocity"] = new[] { ("m/s", "max_velocity_ms") },
            ["batch_set_offset"] = new[] { ("mm", "offset_mm") },
            ["add_change_insulation"] = new[] { ("mm", "thickness_mm") },
            ["find_elements_near"] = new[] { ("mm", "radius_mm") },
            ["auto_size_mep"] = new[] { ("m/s", "target_velocity_ms") },
            ["move_elements"] = new[] { ("mm", "offset_x"), ("ft", "offset_x") },
            ["override_category_color"] = new[] { ("%", "transparency") },
        };

        public static void Enrich(List<ToolCallRequest> toolCalls, PromptContext ctx)
        {
            if (toolCalls == null || ctx == null) return;

            foreach (var tc in toolCalls)
            {
                if (tc.Arguments == null)
                    tc.Arguments = new Dictionary<string, object>();

                InjectLevel(tc, ctx);
                InjectSystem(tc, ctx);
                InjectCategory(tc, ctx);
                InjectDryRun(tc, ctx);
                InjectNumericParams(tc, ctx);
                InjectLimit(tc, ctx);
                InjectElementIds(tc, ctx);
            }
        }

        private static void InjectLevel(ToolCallRequest tc, PromptContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.DetectedLevel)) return;
            if (!LevelAwareTools.Contains(tc.FunctionName)) return;
            if (HasArg(tc, "level")) return;

            tc.Arguments["level"] = ctx.DetectedLevel;
        }

        private static void InjectSystem(ToolCallRequest tc, PromptContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.DetectedSystem)) return;
            if (!SystemAwareTools.Contains(tc.FunctionName)) return;

            if (!HasArg(tc, "system_name") && !HasArg(tc, "system"))
            {
                tc.Arguments["system_name"] = ctx.DetectedSystem;
            }
        }

        private static void InjectCategory(ToolCallRequest tc, PromptContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.DetectedCategory)) return;
            if (!CategoryAwareTools.Contains(tc.FunctionName)) return;
            if (HasArg(tc, "category")) return;

            tc.Arguments["category"] = ctx.DetectedCategory;
        }

        // B1: Inject dry_run=true when user wants preview
        private static void InjectDryRun(ToolCallRequest tc, PromptContext ctx)
        {
            if (!ctx.DetectedDryRun) return;
            if (!DryRunAwareTools.Contains(tc.FunctionName)) return;
            if (HasArg(tc, "dry_run")) return;

            tc.Arguments["dry_run"] = true;
        }

        // B2: Inject numeric parameters from detected numbers
        private static void InjectNumericParams(ToolCallRequest tc, PromptContext ctx)
        {
            if (ctx.DetectedNumbers.Count == 0) return;
            if (!NumericParamMap.TryGetValue(tc.FunctionName, out var mappings)) return;

            foreach (var (unit, paramName) in mappings)
            {
                if (HasArg(tc, paramName)) continue;

                var match = ctx.DetectedNumbers.FirstOrDefault(n => n.Unit == unit);
                if (match.Unit != null)
                {
                    tc.Arguments[paramName] = match.Value;
                }
            }
        }

        // B3: Inject limit when user specified a cap
        private static void InjectLimit(ToolCallRequest tc, PromptContext ctx)
        {
            if (!ctx.DetectedLimit.HasValue) return;
            if (!LimitAwareTools.Contains(tc.FunctionName)) return;
            if (HasArg(tc, "limit") || HasArg(tc, "max_results") || HasArg(tc, "count")) return;

            tc.Arguments["limit"] = ctx.DetectedLimit.Value;
        }

        // B4: Inject element IDs from prompt or selection
        private static void InjectElementIds(ToolCallRequest tc, PromptContext ctx)
        {
            if (ctx.DetectedElementIds.Count == 0) return;
            if (!ElementIdAwareTools.Contains(tc.FunctionName)) return;
            if (HasArg(tc, "element_ids") || HasArg(tc, "elementIds") || HasArg(tc, "ids")) return;

            tc.Arguments["element_ids"] = ctx.DetectedElementIds.Select(id => id.ToString()).ToList();
        }

        private static bool HasArg(ToolCallRequest tc, string key)
        {
            if (!tc.Arguments.TryGetValue(key, out var val)) return false;
            if (val == null) return false;
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Null || je.ValueKind == JsonValueKind.Undefined) return false;
                if (je.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(je.GetString())) return false;
            }
            if (val is string s && string.IsNullOrWhiteSpace(s)) return false;
            return true;
        }
    }
}
