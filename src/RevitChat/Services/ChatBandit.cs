using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitChat.Services
{
    /// <summary>
    /// Epsilon-greedy contextual bandit for tool selection.
    /// State = discretized PromptContext features; Action = tool name; Reward = user feedback.
    /// Learns online and persists Q-table to JSON.
    /// </summary>
    public static class ChatBandit
    {
        private static readonly object _lock = new();
        private static Dictionary<string, Dictionary<string, QEntry>> _qTable = new(StringComparer.OrdinalIgnoreCase);
        private static string _filePath;
        private static int _totalInteractions;

        private const double Alpha = 0.15;
        private const double EpsilonStart = 0.3;
        private const double EpsilonMin = 0.05;
        private const double EpsilonDecay = 0.995;
        private const int MinSamplesForUse = 3;

        public static double Epsilon { get; private set; } = EpsilonStart;
        public static int TotalEntries { get { lock (_lock) { return _qTable.Count; } } }

        public static void Initialize(string dllDirectory)
        {
            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);
            var user = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(dir, $"bandit_qtable_{user}.json");
            Load();
        }

        /// <summary>
        /// Build a discretized state key from PromptContext.
        /// Format: "{intentBucket}_{hasCat}_{hasSys}_{topKwGroup}"
        /// </summary>
        public static string BuildStateKey(PromptContext ctx, string topKeywordGroup = null)
        {
            if (ctx == null) return "unknown_0_0_none";

            var intent = ctx.PrimaryIntent.ToString();
            var hasCat = string.IsNullOrEmpty(ctx.DetectedCategory) ? "0" : "1";
            var hasSys = string.IsNullOrEmpty(ctx.DetectedSystem) ? "0" : "1";
            var kwGroup = string.IsNullOrWhiteSpace(topKeywordGroup) ? "none" : topKeywordGroup;

            return $"{intent}_{hasCat}_{hasSys}_{kwGroup}";
        }

        /// <summary>
        /// Get Q-value based tool score boosts for each tool.
        /// Returns empty dict if insufficient data.
        /// </summary>
        public static Dictionary<string, double> GetToolScores(string stateKey)
        {
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(stateKey)) return scores;

            lock (_lock)
            {
                if (!_qTable.TryGetValue(stateKey, out var actions))
                    return scores;

                foreach (var kvp in actions)
                {
                    if (kvp.Value.Count >= MinSamplesForUse)
                        scores[kvp.Key] = kvp.Value.QValue;
                }
            }

            return scores;
        }

        /// <summary>
        /// Apply bandit scores as additive adjustments to intent scores.
        /// Invoked from PromptAnalyzer scoring pipeline.
        /// </summary>
        public static void ApplyToToolWeights(
            Dictionary<string, double> toolWeights,
            string stateKey)
        {
            var scores = GetToolScores(stateKey);
            if (scores.Count == 0) return;

            foreach (var kvp in scores)
            {
                toolWeights.TryGetValue(kvp.Key, out double current);
                toolWeights[kvp.Key] = current + kvp.Value;
            }
        }

        /// <summary>
        /// Record reward for a (state, tool) pair.
        /// </summary>
        public static void RecordReward(string stateKey, string toolName, double reward)
        {
            if (string.IsNullOrEmpty(stateKey) || string.IsNullOrEmpty(toolName)) return;

            lock (_lock)
            {
                if (!_qTable.TryGetValue(stateKey, out var actions))
                {
                    actions = new Dictionary<string, QEntry>(StringComparer.OrdinalIgnoreCase);
                    _qTable[stateKey] = actions;
                }

                if (!actions.TryGetValue(toolName, out var entry))
                {
                    entry = new QEntry();
                    actions[toolName] = entry;
                }

                // Q-learning update: Q(s,a) += alpha * (reward - Q(s,a))
                entry.QValue += Alpha * (reward - entry.QValue);
                entry.Count++;
                entry.TotalReward += reward;
                entry.LastUpdated = DateTime.UtcNow.ToString("o");

                _totalInteractions++;
                Epsilon = Math.Max(EpsilonMin, Epsilon * EpsilonDecay);

                SaveDebounced();
            }
        }

        /// <summary>
        /// Record positive signal: tool executed successfully without correction.
        /// </summary>
        public static void RecordSuccess(string stateKey, string toolName)
            => RecordReward(stateKey, toolName, 1.0);

        /// <summary>
        /// Record negative signal: user corrected or rejected the tool call.
        /// </summary>
        public static void RecordCorrection(string stateKey, string toolName)
            => RecordReward(stateKey, toolName, -1.0);

        /// <summary>
        /// Record retry signal: user rephrased the prompt.
        /// </summary>
        public static void RecordRetry(string stateKey, string toolName)
            => RecordReward(stateKey, toolName, -0.5);

        /// <summary>
        /// Record thumbs-up signal from explicit user feedback.
        /// </summary>
        public static void RecordThumbsUp(string stateKey, string toolName)
            => RecordReward(stateKey, toolName, 0.5);

        /// <summary>
        /// Record thumbs-down signal from explicit user feedback.
        /// </summary>
        public static void RecordThumbsDown(string stateKey, string toolName)
            => RecordReward(stateKey, toolName, -1.0);

        #region Persistence

        private static DateTime _lastSave = DateTime.MinValue;
        private static void SaveDebounced()
        {
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < 10) return;
            _lastSave = DateTime.UtcNow;
            Save();
        }

        public static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath)) return;
                var data = new BanditData
                {
                    Epsilon = Epsilon,
                    TotalInteractions = _totalInteractions,
                    QTable = _qTable
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatBandit] Save: {ex.Message}");
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
                        var data = JsonSerializer.Deserialize<BanditData>(json);
                        if (data != null)
                        {
                            _qTable = data.QTable ?? new(StringComparer.OrdinalIgnoreCase);
                            Epsilon = data.Epsilon > 0 ? data.Epsilon : EpsilonStart;
                            _totalInteractions = data.TotalInteractions;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatBandit] Load: {ex.Message}");
                    _qTable = new(StringComparer.OrdinalIgnoreCase);
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

        #endregion
    }

    internal class BanditData
    {
        [JsonPropertyName("epsilon")]
        public double Epsilon { get; set; }

        [JsonPropertyName("total_interactions")]
        public int TotalInteractions { get; set; }

        [JsonPropertyName("q_table")]
        public Dictionary<string, Dictionary<string, QEntry>> QTable { get; set; }
    }

    internal class QEntry
    {
        [JsonPropertyName("q")]
        public double QValue { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("total_reward")]
        public double TotalReward { get; set; }

        [JsonPropertyName("last_updated")]
        public string LastUpdated { get; set; }
    }
}
