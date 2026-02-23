using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RevitChat.Models;

namespace RevitChat.Services
{
    public class ContextCollectionService
    {
        public async Task<string> CollectAsync(
            string userText,
            Func<List<ToolCallRequest>, Task<Dictionary<string, string>>> executeFunc)
        {
            var lower = userText?.ToLowerInvariant() ?? "";
            var needsView = lower.Contains("current view") || lower.Contains("active view")
                || lower.Contains("view hiện tại") || lower.Contains("view hiên tại")
                || lower.Contains("view đang mở") || lower.Contains("trong view")
                || lower.Contains("in view") || lower.Contains("on view");
            var needsSelection = lower.Contains("selected") || lower.Contains("selection")
                || lower.Contains("đang chọn") || lower.Contains("được chọn");
            var needsLevels = lower.Contains("level") || lower.Contains("tầng")
                || lower.Contains("cao độ") || lower.Contains("floor");
            var needsTagInfo = lower.Contains("tag") || lower.Contains("ghi chú")
                || lower.Contains("annotation") || lower.Contains("chú thích")
                || lower.Contains("chưa tag") || lower.Contains("untagged");

            if (!needsView && !needsSelection && !needsLevels && !needsTagInfo) return "";

            var calls = new List<ToolCallRequest>();
            if (needsView || needsTagInfo)
                calls.Add(MakeCall("get_current_view"));
            if (needsSelection)
                calls.Add(MakeCall("get_current_selection"));
            if (needsLevels)
                calls.Add(MakeCall("get_levels_detailed"));

            if (calls.Count == 0) return "";

            var results = await executeFunc(calls);

            var sb = new StringBuilder();
            foreach (var kvp in results)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                var snippet = kvp.Value.Length > 2000 ? kvp.Value[..2000] + "...[TRUNCATED]" : kvp.Value;
                sb.AppendLine($"{kvp.Key}: {snippet}");
            }

            if (needsLevels && results.Keys.Any(k => k.Contains("get_levels_detailed")))
                sb.AppendLine("IMPORTANT: Use the EXACT level names from the list above when calling tools. Do NOT use the user's approximate name.");

            if (needsTagInfo)
                sb.AppendLine("HINT: Use get_tag_rules to find tag format patterns, and get_available_tag_types to list available tag families before tagging.");

            return sb.ToString().Trim();
        }

        private static ToolCallRequest MakeCall(string funcName) => new()
        {
            ToolCallId = $"pre_{funcName}_{Guid.NewGuid():N}"[..32],
            FunctionName = funcName,
            Arguments = new Dictionary<string, object>()
        };
    }
}
