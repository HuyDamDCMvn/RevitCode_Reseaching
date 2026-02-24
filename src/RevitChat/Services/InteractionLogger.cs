using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitChat.Models;

namespace RevitChat.Services
{
    public class InteractionRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("intent")] public string Intent { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; }
        [JsonPropertyName("system")] public string System { get; set; }
        [JsonPropertyName("level")] public string Level { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCallRecord> ToolCalls { get; set; } = new();
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("feedback")] public string Feedback { get; set; }
        [JsonPropertyName("retry")] public bool WasRetried { get; set; }
        [JsonPropertyName("response_ms")] public long ResponseMs { get; set; }
    }

    public class ToolCallRecord
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; } = new();
        [JsonPropertyName("result_ok")] public bool ResultOk { get; set; }
        [JsonPropertyName("result_chars")] public int ResultChars { get; set; }
    }

    /// <summary>
    /// Logs every interaction for offline analysis, self-learning, and adaptive weight tuning.
    /// Writes to a per-user JSONL file (one JSON object per line) for easy appending.
    /// </summary>
    public static class InteractionLogger
    {
        private static readonly object _lock = new();
        private static string _filePath;
        private static string _projectHash;
        private const int MaxInMemoryRecords = 200;
        private static readonly List<InteractionRecord> _recentRecords = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Initialize(string dllDirectory)
        {
            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);
            var user = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(dir, $"interactions_{user}.jsonl");
            LoadRecent();
        }

        public static void SetProjectContext(string projectName)
        {
            _projectHash = SanitizeFileName(projectName ?? "default");
        }

        public static InteractionRecord BeginInteraction(string prompt, PromptContext ctx)
        {
            return new InteractionRecord
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Prompt = prompt,
                Intent = ctx?.PrimaryIntent.ToString(),
                Category = ctx?.DetectedCategory,
                System = ctx?.DetectedSystem,
                Level = ctx?.DetectedLevel
            };
        }

        public static void RecordToolCalls(InteractionRecord record,
            List<ToolCallRequest> calls, Dictionary<string, string> results)
        {
            if (record == null || calls == null) return;
            foreach (var tc in calls)
            {
                string resultValue = null;
                var hasResult = results != null &&
                    results.TryGetValue(tc.ToolCallId ?? tc.FunctionName, out resultValue);
                var resultOk = hasResult &&
                    !string.IsNullOrEmpty(resultValue) && !resultValue.Contains("\"error\"");
                record.ToolCalls.Add(new ToolCallRecord
                {
                    Name = tc.FunctionName,
                    Args = tc.Arguments,
                    ResultOk = resultOk,
                    ResultChars = resultValue?.Length ?? 0
                });
            }
        }

        public static void EndInteraction(InteractionRecord record, bool success, string error = null)
        {
            if (record == null) return;
            record.Success = success;
            record.Error = error;
            Append(record);
        }

        public static void MarkFeedback(string interactionId, string feedback)
        {
            lock (_lock)
            {
                var rec = _recentRecords.FirstOrDefault(r => r.Id == interactionId);
                if (rec != null) rec.Feedback = feedback;
            }
        }

        public static void MarkRetried(InteractionRecord record)
        {
            if (record != null) record.WasRetried = true;
        }

        /// <summary>
        /// Get recent records for analysis (last N).
        /// </summary>
        public static List<InteractionRecord> GetRecent(int count = 50)
        {
            lock (_lock)
            {
                return _recentRecords.TakeLast(Math.Min(count, _recentRecords.Count)).ToList();
            }
        }

        /// <summary>
        /// Get success rate for a specific tool.
        /// </summary>
        public static (int total, int success) GetToolStats(string toolName, int lookback = 100)
        {
            lock (_lock)
            {
                var recent = _recentRecords.TakeLast(lookback).ToList();
                int total = 0, success = 0;
                foreach (var r in recent)
                {
                    foreach (var tc in r.ToolCalls)
                    {
                        if (string.Equals(tc.Name, toolName, StringComparison.OrdinalIgnoreCase))
                        {
                            total++;
                            if (tc.ResultOk) success++;
                        }
                    }
                }
                return (total, success);
            }
        }

        /// <summary>
        /// Get the most common intent→tool patterns from successful interactions.
        /// </summary>
        public static Dictionary<string, List<string>> GetSuccessfulPatterns(int lookback = 100)
        {
            lock (_lock)
            {
                var patterns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var recent = _recentRecords.Where(r => r.Success).TakeLast(lookback);
                foreach (var r in recent)
                {
                    var key = r.Intent ?? "Unknown";
                    var tools = r.ToolCalls.Where(t => t.ResultOk).Select(t => t.Name).ToList();
                    if (tools.Count > 0)
                    {
                        if (!patterns.ContainsKey(key)) patterns[key] = new();
                        patterns[key].AddRange(tools);
                    }
                }
                return patterns;
            }
        }

        private static void Append(InteractionRecord record)
        {
            lock (_lock)
            {
                _recentRecords.Add(record);
                if (_recentRecords.Count > MaxInMemoryRecords)
                    _recentRecords.RemoveRange(0, _recentRecords.Count - MaxInMemoryRecords);

                try
                {
                    if (!string.IsNullOrEmpty(_filePath))
                    {
                        var line = JsonSerializer.Serialize(record, JsonOpts);
                        File.AppendAllText(_filePath, line + "\n");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InteractionLogger] Append: {ex.Message}");
                }
            }
        }

        private static void LoadRecent()
        {
            lock (_lock)
            {
                _recentRecords.Clear();
                try
                {
                    if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
                    {
                        var lines = File.ReadAllLines(_filePath);
                        var start = Math.Max(0, lines.Length - MaxInMemoryRecords);
                        for (int i = start; i < lines.Length; i++)
                        {
                            if (string.IsNullOrWhiteSpace(lines[i])) continue;
                            var rec = JsonSerializer.Deserialize<InteractionRecord>(lines[i]);
                            if (rec != null) _recentRecords.Add(rec);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[InteractionLogger] LoadRecent: {ex.Message}");
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "default";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.ToLowerInvariant();
        }
    }
}
