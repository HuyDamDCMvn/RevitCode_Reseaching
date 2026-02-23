using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitChat.Models;

namespace RevitChat.Services
{
    public class ContextCollectionService
    {
        private string _cachedViewContext;
        private DateTime _cacheTime;
        private static readonly TimeSpan ViewCacheTTL = TimeSpan.FromSeconds(30);

        private static readonly string[] SelectionKeywords =
            { "selected", "chọn", "highlighted", "selection", "lựa chọn", "đang chọn", "these", "this", "này" };

        public async Task<string> CollectAsync(
            string userText,
            Func<List<ToolCallRequest>, Task<Dictionary<string, string>>> executeFunc)
        {
            var lower = userText?.ToLowerInvariant() ?? "";
            bool needsSelection = SelectionKeywords.Any(k => lower.Contains(k));
            bool viewCacheValid = _cachedViewContext != null && (DateTime.UtcNow - _cacheTime) < ViewCacheTTL;

            var calls = new List<ToolCallRequest>();
            if (!viewCacheValid)
            {
                calls.Add(MakeCall("get_current_view"));
                calls.Add(MakeCall("get_project_info"));
            }
            if (needsSelection)
                calls.Add(MakeCall("get_current_selection"));

            if (calls.Count == 0)
            {
                var sb2 = new StringBuilder();
                if (!string.IsNullOrEmpty(_cachedViewContext)) sb2.AppendLine(_cachedViewContext);
                AppendKeywordHints(sb2, lower);
                return sb2.ToString().Trim();
            }

            Dictionary<string, string> results;
            try
            {
                results = await executeFunc(calls);
            }
            catch (Exception)
            {
                return string.Empty;
            }

            var contextStr = BuildContextString(results);
            if (!string.IsNullOrEmpty(contextStr) && !needsSelection)
            {
                _cachedViewContext = contextStr;
                _cacheTime = DateTime.UtcNow;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(contextStr))
                sb.AppendLine(contextStr);
            else if (!string.IsNullOrEmpty(_cachedViewContext))
                sb.AppendLine(_cachedViewContext);

            AppendKeywordHints(sb, lower);
            return sb.ToString().Trim();
        }

        private static string BuildContextString(Dictionary<string, string> results)
        {
            string viewName = null, viewType = null, levelName = null;
            int selCount = 0;
            var selCategories = new List<string>();
            var selIds = new List<long>();
            string docName = null;
            bool? workshared = null;

            foreach (var kvp in results)
            {
                var val = kvp.Value;
                if (string.IsNullOrWhiteSpace(val) || val.Contains("\"error\"")) continue;

                if (kvp.Key.Contains("get_current_view"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(val);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("name", out var n)) viewName = n.GetString();
                        if (root.TryGetProperty("view_type", out var vt)) viewType = vt.GetString();
                        if (root.TryGetProperty("level", out var l) && l.GetString() != "-") levelName = l.GetString();
                    }
                    catch { }
                }
                else if (kvp.Key.Contains("get_current_selection"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(val);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("count", out var c)) selCount = c.GetInt32();
                        if (root.TryGetProperty("summary_by_category", out var catArr))
                        {
                            foreach (var item in catArr.EnumerateArray().Take(3))
                            {
                                var cat = item.TryGetProperty("category", out var catProp) ? catProp.GetString() : null;
                                var cnt = item.TryGetProperty("count", out var cntProp) ? cntProp.GetInt32() : 0;
                                if (!string.IsNullOrEmpty(cat)) selCategories.Add($"{cnt} {cat}");
                            }
                        }
                        if (root.TryGetProperty("elements", out var elemArr))
                        {
                            foreach (var item in elemArr.EnumerateArray().Take(10))
                            {
                                if (item.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var id))
                                    selIds.Add(id);
                            }
                        }
                    }
                    catch { }
                }
                else if (kvp.Key.Contains("get_project_info"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(val);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("file_path", out var fp))
                        {
                            var path = fp.GetString();
                            docName = !string.IsNullOrEmpty(path) && path != "-"
                                ? System.IO.Path.GetFileName(path) : null;
                        }
                        if (root.TryGetProperty("is_workshared", out var ws)) workshared = ws.GetBoolean();
                    }
                    catch { }
                }
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(viewName))
                parts.Add($"View: \"{viewName}\" ({viewType ?? "?"})");
            if (!string.IsNullOrEmpty(levelName))
                parts.Add($"Level: {levelName}");
            if (selCount > 0)
            {
                var selStr = selCategories.Count > 0
                    ? string.Join(", ", selCategories)
                    : $"{selCount} element(s)";
                if (selIds.Count > 0)
                    selStr += $" | IDs: [{string.Join(", ", selIds)}]";
                parts.Add($"Selection: {selStr}");
            }
            else if (selCount == 0 && results.Keys.Any(k => k.Contains("get_current_selection")))
                parts.Add("Selection: none");
            if (!string.IsNullOrEmpty(docName))
            {
                var docStr = workshared == true ? $"{docName} (workshared)" : docName;
                parts.Add($"Doc: {docStr}");
            }

            return parts.Count > 0 ? string.Join(" | ", parts) : null;
        }

        private void AppendKeywordHints(StringBuilder sb, string lower)
        {
            if (lower.Contains("level") || lower.Contains("tầng")
                || lower.Contains("cao độ") || lower.Contains("floor"))
                sb.AppendLine("HINT: Use get_levels_detailed for exact level names.");

            if (lower.Contains("tag") || lower.Contains("ghi chú")
                || lower.Contains("annotation") || lower.Contains("chú thích")
                || lower.Contains("chưa tag") || lower.Contains("untagged"))
                sb.AppendLine("HINT: Use get_tag_rules for tag format patterns, get_available_tag_types to list tag families.");
        }

        private static ToolCallRequest MakeCall(string funcName) => new()
        {
            ToolCallId = $"pre_{funcName}_{Guid.NewGuid():N}",
            FunctionName = funcName,
            Arguments = new Dictionary<string, object>()
        };
    }
}
