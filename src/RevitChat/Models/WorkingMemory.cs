using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitChat.Models
{
    public class WorkingMemory
    {
        public List<long> LastElementIds { get; set; } = new();
        public string LastCategory { get; set; }
        public string LastLevelName { get; set; }
        public int LastCount { get; set; }
        public string LastViewName { get; set; }
        public string LastToolName { get; set; }

        private const int MaxStoredIds = 50;

        public void UpdateFromToolResult(string toolName, string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson)) return;

            LastToolName = toolName;

            try
            {
                if (toolName == "get_current_view")
                {
                    var match = Regex.Match(resultJson, @"View:\s*(.+?)(?:\n|$)");
                    if (match.Success) LastViewName = match.Groups[1].Value.Trim();
                    return;
                }

                if (toolName == "count_elements")
                {
                    var countMatch = Regex.Match(resultJson, @"""total""\s*:\s*(\d+)");
                    if (countMatch.Success)
                        LastCount = int.Parse(countMatch.Groups[1].Value);
                    return;
                }

                ExtractElementIds(resultJson);
                ExtractCategory(resultJson);
                ExtractLevel(resultJson);
            }
            catch { }
        }

        private void ExtractElementIds(string text)
        {
            var ids = new List<long>();
            foreach (Match m in Regex.Matches(text, @"(?:ElementId|Element ID|ID|Id|id|#)[:\s]*(\d{4,})"))
            {
                if (long.TryParse(m.Groups[1].Value, out var id))
                    ids.Add(id);
            }
            if (ids.Count > 0)
            {
                LastElementIds = ids.Take(MaxStoredIds).ToList();
                LastCount = ids.Count;
            }
        }

        private void ExtractCategory(string text)
        {
            var match = Regex.Match(text, @"Category:\s*(.+?)(?:\n|$)");
            if (match.Success)
                LastCategory = match.Groups[1].Value.Trim();
        }

        private void ExtractLevel(string text)
        {
            var match = Regex.Match(text, @"Level:\s*(.+?)(?:\n|$)");
            if (match.Success)
                LastLevelName = match.Groups[1].Value.Trim();
        }

        public string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Working Memory]");

            if (!string.IsNullOrEmpty(LastCategory))
                sb.AppendLine($"Last category: {LastCategory}");

            if (LastCount > 0)
                sb.AppendLine($"Last result count: {LastCount}");

            if (!string.IsNullOrEmpty(LastLevelName))
                sb.AppendLine($"Last level: {LastLevelName}");

            if (!string.IsNullOrEmpty(LastViewName))
                sb.AppendLine($"Current view: {LastViewName}");

            if (LastElementIds.Count > 0)
            {
                var shown = LastElementIds.Take(20);
                sb.AppendLine($"Element IDs (first {shown.Count()}): [{string.Join(", ", shown)}]");
                if (LastElementIds.Count > 20)
                    sb.AppendLine($"({LastElementIds.Count} total IDs available)");
            }

            return sb.ToString();
        }

        public void Clear()
        {
            LastElementIds.Clear();
            LastCategory = null;
            LastLevelName = null;
            LastCount = 0;
            LastViewName = null;
            LastToolName = null;
        }

        public static string CompressToolResult(string toolName, string result)
        {
            if (string.IsNullOrWhiteSpace(result)) return result;
            if (result.Length <= 2000) return result;

            var lines = result.Split('\n');
            var sb = new StringBuilder();

            sb.AppendLine($"[{toolName} result summary — {lines.Length} lines, {result.Length} chars total]");

            int shown = 0;
            foreach (var line in lines)
            {
                if (shown >= 40) break;
                sb.AppendLine(line);
                shown++;
            }

            if (lines.Length > 40)
                sb.AppendLine($"...[{lines.Length - 40} more lines truncated. Summarize what you have.]");

            return sb.ToString();
        }
    }
}
