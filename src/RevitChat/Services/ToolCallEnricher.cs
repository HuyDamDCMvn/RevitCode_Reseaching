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

        public static void Enrich(List<ToolCallRequest> toolCalls, PromptContext ctx)
        {
            if (toolCalls == null || ctx == null) return;

            foreach (var tc in toolCalls)
            {
                if (tc.Arguments == null)
                    tc.Arguments = new Dictionary<string, object>();

                InjectLevel(tc, ctx);
                InjectSystem(tc, ctx);
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
                var argName = tc.FunctionName.Contains("system") ? "system_name" : "system_name";
                tc.Arguments[argName] = ctx.DetectedSystem;
            }
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
