using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitChat.Models;

namespace RevitChat.Services
{
    public static class ChatGuardService
    {
        private static readonly HashSet<string> RiskyTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "delete_elements", "set_parameter_value", "rename_elements",
            "move_elements", "copy_elements", "mirror_elements",
            "duplicate_views", "duplicate_sheets", "resize_mep_elements",
            "split_mep_elements", "delete_levels", "create_level",
            "apply_parameter_formula", "transfer_parameters", "batch_update_parameters",
            "place_family_instance", "load_family",
            "add_project_parameter", "override_element_color",
            "override_category_color", "override_color_by_filter",
            "create_openings", "purge_unused_elements", "batch_rename_pattern",
            "measure_distance_to_slab"
        };

        public static bool IsEchoResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var t = text.Trim();
            if (t.StartsWith("[Executing") || t == "(tool call)" || t == "...")
                return true;
            if (t.Contains("<tool_call>") || t.Contains("</tool_call>"))
                return true;
            if (t.Contains("\"arguments\"") && t.Contains("\"name\"") && t.Length < 500)
                return true;
            if (Regex.IsMatch(t, @"^\s*[\{\};,"":\[\]]+\s*$"))
                return true;
            return false;
        }

        public static bool IsConfirmMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.Trim().ToLowerInvariant();
            return lower is "yes" or "y" or "ok" or "okay" or "confirm" or "confirmed"
                || lower.Contains("xác nhận") || lower.Contains("đồng ý") || lower.Contains("tiếp tục")
                || lower.Contains("thực hiện") || lower.Contains("làm đi") || lower.Contains("được");
        }

        public static bool IsCancelMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.Trim().ToLowerInvariant();
            return lower is "no" or "n" or "cancel" or "stop"
                || lower.Contains("hủy") || lower.Contains("không") || lower.Contains("dừng");
        }

        public static bool RequiresConfirmation(List<ToolCallRequest> toolCalls, out string prompt)
        {
            prompt = null;
            if (toolCalls == null || toolCalls.Count == 0) return false;

            var risky = toolCalls.Where(IsRiskyToolCall).ToList();
            if (risky.Count == 0) return false;

            var list = string.Join(", ", risky.Select(t => t.FunctionName));
            prompt = $"Bạn có muốn thực hiện thao tác sau không? {list}\nTrả lời 'xác nhận' để tiếp tục hoặc 'hủy' để bỏ.";
            return true;
        }

        public static bool IsRiskyToolCall(ToolCallRequest call)
        {
            if (!RiskyTools.Contains(call.FunctionName)) return false;
            if (call.Arguments != null && call.Arguments.TryGetValue("dry_run", out var dryObj))
            {
                if (dryObj is JsonElement je && je.ValueKind == JsonValueKind.True)
                    return false;
                if (dryObj is bool b && b) return false;
            }
            if (call.FunctionName.Equals("resize_mep_elements", StringComparison.OrdinalIgnoreCase)) return true;
            if (call.Arguments == null || call.Arguments.Count == 0) return true;

            if (call.Arguments.ContainsKey("element_id") || call.Arguments.ContainsKey("room_id")
                || call.Arguments.ContainsKey("view_id") || call.Arguments.ContainsKey("level_id"))
                return false;

            if (call.Arguments.TryGetValue("element_ids", out var idsObj))
            {
                if (idsObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
                    return je.GetArrayLength() > 20;
                return false;
            }

            return true;
        }

        public static int GetTotalChars(Dictionary<string, string> results)
        {
            if (results == null) return 0;
            int total = 0;
            foreach (var kvp in results)
                total += kvp.Value?.Length ?? 0;
            return total;
        }
    }
}
