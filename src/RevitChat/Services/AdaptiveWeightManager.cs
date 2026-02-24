using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitChat.Services
{
    /// <summary>
    /// Dynamically adjusts keyword→intent weights based on success/failure of interactions.
    /// Persists learned adjustments to JSON, loads on startup.
    /// Works as a modifier on top of PromptAnalyzer's static weights.
    /// </summary>
    public static class AdaptiveWeightManager
    {
        private static readonly object _lock = new();
        private static Dictionary<string, WeightAdjustment> _adjustments = new(StringComparer.OrdinalIgnoreCase);
        private static string _filePath;
        private const double LearningRate = 0.1;
        private const double MaxBoost = 2.0;
        private const double MinBoost = -1.5;
        private const int MinSamplesForAdjustment = 3;

        public static void Initialize(string dllDirectory)
        {
            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);
            var user = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(dir, $"adaptive_weights_{user}.json");
            Load();
        }

        /// <summary>
        /// Record a positive signal: this keyword→intent mapping led to a successful tool call.
        /// </summary>
        public static void RecordSuccess(string intent, string keyword, string toolName)
        {
            if (string.IsNullOrWhiteSpace(intent) || string.IsNullOrWhiteSpace(keyword)) return;
            var key = $"{intent}:{keyword}";
            lock (_lock)
            {
                if (!_adjustments.TryGetValue(key, out var adj))
                {
                    adj = new WeightAdjustment { Intent = intent, Keyword = keyword };
                    _adjustments[key] = adj;
                }
                adj.SuccessCount++;
                adj.LastTool = toolName;
                adj.Adjustment = CalculateAdjustment(adj);
                adj.LastUpdated = DateTime.UtcNow.ToString("o");
                SaveDebounced();
            }
        }

        /// <summary>
        /// Record a negative signal: this keyword→intent mapping led to a failed/corrected tool call.
        /// </summary>
        public static void RecordFailure(string intent, string keyword)
        {
            if (string.IsNullOrWhiteSpace(intent) || string.IsNullOrWhiteSpace(keyword)) return;
            var key = $"{intent}:{keyword}";
            lock (_lock)
            {
                if (!_adjustments.TryGetValue(key, out var adj))
                {
                    adj = new WeightAdjustment { Intent = intent, Keyword = keyword };
                    _adjustments[key] = adj;
                }
                adj.FailureCount++;
                adj.Adjustment = CalculateAdjustment(adj);
                adj.LastUpdated = DateTime.UtcNow.ToString("o");
                SaveDebounced();
            }
        }

        /// <summary>
        /// Get the weight adjustment for a specific intent+keyword pair.
        /// Returns 0.0 if no data or insufficient samples.
        /// </summary>
        public static double GetAdjustment(string intent, string keyword)
        {
            var key = $"{intent}:{keyword}";
            lock (_lock)
            {
                if (_adjustments.TryGetValue(key, out var adj) &&
                    (adj.SuccessCount + adj.FailureCount) >= MinSamplesForAdjustment)
                    return adj.Adjustment;
            }
            return 0.0;
        }

        /// <summary>
        /// Apply learned weight adjustments to intent scores.
        /// Called from PromptAnalyzer after initial scoring.
        /// </summary>
        public static void ApplyToScores(Dictionary<PromptIntent, int> scores,
            string stripped, string spaced)
        {
            lock (_lock)
            {
                if (_adjustments.Count == 0) return;

                foreach (var kvp in _adjustments)
                {
                    if ((kvp.Value.SuccessCount + kvp.Value.FailureCount) < MinSamplesForAdjustment)
                        continue;

                    if (!Enum.TryParse<PromptIntent>(kvp.Value.Intent, out var intent))
                        continue;

                    var kw = kvp.Value.Keyword.ToLowerInvariant();
                    if (stripped.Contains(kw) || spaced.Contains(kw))
                    {
                        var adj = (int)Math.Round(kvp.Value.Adjustment);
                        if (adj != 0)
                        {
                            scores.TryGetValue(intent, out int current);
                            scores[intent] = Math.Max(0, current + adj);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get tool suggestions that have high success rates for the given intent.
        /// </summary>
        public static List<string> GetHighSuccessTools(string intent, int topK = 3)
        {
            lock (_lock)
            {
                return _adjustments.Values
                    .Where(a => a.Intent == intent && a.SuccessCount >= MinSamplesForAdjustment
                                && !string.IsNullOrEmpty(a.LastTool))
                    .OrderByDescending(a => a.SuccessRate)
                    .Take(topK)
                    .Select(a => a.LastTool)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static int TotalAdjustments
        {
            get { lock (_lock) { return _adjustments.Count; } }
        }

        private static double CalculateAdjustment(WeightAdjustment adj)
        {
            int total = adj.SuccessCount + adj.FailureCount;
            if (total < MinSamplesForAdjustment) return 0;

            double successRate = (double)adj.SuccessCount / total;
            double boost = (successRate - 0.5) * 2.0 * LearningRate * Math.Log(total + 1);
            return Math.Clamp(boost, MinBoost, MaxBoost);
        }

        private static DateTime _lastSave = DateTime.MinValue;
        private static void SaveDebounced()
        {
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < 10) return;
            _lastSave = DateTime.UtcNow;
            Save();
        }

        private static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath)) return;
                var json = JsonSerializer.Serialize(_adjustments.Values.ToList(), new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AdaptiveWeightManager] Save: {ex.Message}");
            }
        }

        private static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
                    {
                        var json = File.ReadAllText(_filePath);
                        var list = JsonSerializer.Deserialize<List<WeightAdjustment>>(json);
                        if (list != null)
                        {
                            _adjustments = list.ToDictionary(
                                a => $"{a.Intent}:{a.Keyword}",
                                a => a,
                                StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AdaptiveWeightManager] Load: {ex.Message}");
                    _adjustments = new(StringComparer.OrdinalIgnoreCase);
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

    public class WeightAdjustment
    {
        [JsonPropertyName("intent")] public string Intent { get; set; }
        [JsonPropertyName("keyword")] public string Keyword { get; set; }
        [JsonPropertyName("success_count")] public int SuccessCount { get; set; }
        [JsonPropertyName("failure_count")] public int FailureCount { get; set; }
        [JsonPropertyName("adjustment")] public double Adjustment { get; set; }
        [JsonPropertyName("last_tool")] public string LastTool { get; set; }
        [JsonPropertyName("last_updated")] public string LastUpdated { get; set; }

        [JsonIgnore]
        public double SuccessRate => (SuccessCount + FailureCount) > 0
            ? (double)SuccessCount / (SuccessCount + FailureCount) : 0;
    }
}
