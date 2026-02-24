using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChat.Services
{
    /// <summary>
    /// Orchestrates all learning signals after each interaction.
    /// Coordinates: AdaptiveWeightManager, ChatBandit, DynamicFewShotSelector,
    /// EmbeddingMatcher, InteractionLogger, ChatToolClassifier, and SelfTrainingService.
    ///
    /// Tracks learning momentum and triggers self-improvement automatically:
    /// - After N new confirmed patterns, auto-augment training data
    /// - When ANN retraining threshold is reached, export batch + notify
    /// - Periodically consolidate weak signals into strong patterns
    /// </summary>
    public static class SelfLearningOrchestrator
    {
        private static string _dllDirectory;
        private static string _statsPath;
        private static LearningStats _stats = new();
        private static readonly object _lock = new();

        private const int AugmentThreshold = 10;
        private const int RetrainThreshold = 100;
        private const int ConsolidateThreshold = 25;

        public static event Action<string> LearningEvent;

        public static void Initialize(string dllDirectory)
        {
            _dllDirectory = dllDirectory;
            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);
            var user = SanitizeFileName(Environment.UserName);
            _statsPath = Path.Combine(dir, $"learning_stats_{user}.json");
            LoadStats();
        }

        /// <summary>
        /// Record a complete interaction outcome. Call after tool execution completes.
        /// This is the main entry point for the self-learning pipeline.
        /// </summary>
        public static void RecordInteraction(InteractionOutcome outcome)
        {
            if (outcome == null) return;

            lock (_lock)
            {
                _stats.TotalInteractions++;
                if (outcome.Success)
                    _stats.SuccessfulInteractions++;
                else
                    _stats.FailedInteractions++;

                _stats.NewSinceLastAugment++;
                _stats.NewSinceLastRetrain++;
                _stats.NewSinceLastConsolidate++;
                _stats.LastInteractionTime = DateTime.UtcNow.ToString("o");
            }

            // 1. Update bandit Q-table
            if (!string.IsNullOrEmpty(outcome.BanditStateKey))
            {
                foreach (var tool in outcome.ToolNames)
                {
                    if (outcome.Success)
                        ChatBandit.RecordSuccess(outcome.BanditStateKey, tool);
                    else if (outcome.WasCorrection)
                        ChatBandit.RecordCorrection(outcome.BanditStateKey, tool);
                    else
                        ChatBandit.RecordReward(outcome.BanditStateKey, tool, -0.3);
                }
            }

            // 2. Update adaptive weights
            if (!string.IsNullOrEmpty(outcome.Intent) && !string.IsNullOrEmpty(outcome.Keyword))
            {
                foreach (var tool in outcome.ToolNames)
                {
                    if (outcome.Success)
                        AdaptiveWeightManager.RecordSuccess(outcome.Intent, outcome.Keyword, tool);
                    else
                        AdaptiveWeightManager.RecordFailure(outcome.Intent, outcome.Keyword);
                }
            }

            // 3. Update few-shot selector (successful only)
            if (outcome.Success && outcome.ToolCalls?.Count > 0)
            {
                foreach (var tc in outcome.ToolCalls)
                {
                    DynamicFewShotSelector.RecordSuccess(
                        outcome.Prompt, tc.Name, tc.Args,
                        outcome.Intent, outcome.Category);
                }
            }

            // 4. Check thresholds for auto-improvement
            CheckAndTriggerImprovements();

            SaveStatsDebounced();
        }

        /// <summary>
        /// Record explicit user feedback (thumbs up/down).
        /// </summary>
        public static void RecordFeedback(string banditStateKey, List<string> toolNames, bool positive)
        {
            if (string.IsNullOrEmpty(banditStateKey) || toolNames == null) return;

            foreach (var tool in toolNames)
            {
                if (positive)
                    ChatBandit.RecordThumbsUp(banditStateKey, tool);
                else
                    ChatBandit.RecordThumbsDown(banditStateKey, tool);
            }

            lock (_lock)
            {
                if (positive)
                    _stats.ThumbsUpCount++;
                else
                    _stats.ThumbsDownCount++;
            }

            SaveStatsDebounced();
        }

        /// <summary>
        /// Check learning stats and trigger improvements when thresholds are met.
        /// </summary>
        private static void CheckAndTriggerImprovements()
        {
            bool shouldAugment;
            bool shouldConsolidate;
            bool shouldNotifyRetrain;

            lock (_lock)
            {
                shouldAugment = _stats.NewSinceLastAugment >= AugmentThreshold;
                shouldConsolidate = _stats.NewSinceLastConsolidate >= ConsolidateThreshold;
                shouldNotifyRetrain = _stats.NewSinceLastRetrain >= RetrainThreshold
                    && !_stats.RetrainNotified;
            }

            if (shouldAugment)
            {
                _ = Task.Run(() => AutoAugmentFromConfirmedPatterns());
            }

            if (shouldConsolidate)
            {
                _ = Task.Run(() => ConsolidateWeakSignals());
            }

            if (shouldNotifyRetrain)
            {
                lock (_lock) { _stats.RetrainNotified = true; }
                _ = Task.Run(() => PrepareRetrainingData());
            }
        }

        /// <summary>
        /// Auto-generate augmented training data from recently confirmed patterns.
        /// Creates typo variants, abbreviation variants, and mixed-language variants
        /// to help the chatbot generalize better.
        /// </summary>
        private static void AutoAugmentFromConfirmedPatterns()
        {
            try
            {
                var confirmed = DynamicFewShotSelector.GetConfirmedExamples(minUseCount: 3);
                if (confirmed.Count == 0) return;

                int augmented = 0;
                foreach (var ex in confirmed.Take(20))
                {
                    var variants = GenerateVariants(ex.Prompt);
                    foreach (var variant in variants)
                    {
                        DynamicFewShotSelector.RecordSuccess(
                            variant, ex.ToolName, ex.Args, ex.Intent, ex.Category);
                        augmented++;
                    }
                }

                lock (_lock) { _stats.NewSinceLastAugment = 0; }

                if (augmented > 0)
                {
                    LearningEvent?.Invoke($"Auto-augmented {augmented} variants from {confirmed.Count} confirmed patterns");
                    Debug.WriteLine($"[SelfLearning] Auto-augmented {augmented} variants");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelfLearning] AutoAugment: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate variant prompts for data augmentation.
        /// </summary>
        private static List<string> GenerateVariants(string prompt)
        {
            var variants = new List<string>();
            if (string.IsNullOrWhiteSpace(prompt)) return variants;

            var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return variants;

            // Abbreviation variant
            var abbreviated = string.Join(" ", words.Select(w =>
                w.Length > 6 ? w[..4] + "." : w));
            if (abbreviated != prompt)
                variants.Add(abbreviated);

            // No-diacritics variant (Vietnamese)
            var stripped = PromptAnalyzer.StripDiacriticsPublic(prompt);
            if (stripped != prompt.ToLowerInvariant())
                variants.Add(stripped);

            // Swap word order variant
            if (words.Length >= 3)
            {
                var rng = new Random();
                var idx = rng.Next(0, words.Length - 1);
                var swapped = (string[])words.Clone();
                (swapped[idx], swapped[idx + 1]) = (swapped[idx + 1], swapped[idx]);
                variants.Add(string.Join(" ", swapped));
            }

            return variants;
        }

        /// <summary>
        /// Consolidate weak learning signals into stronger patterns.
        /// Examines low-confidence entries and either promotes or prunes them.
        /// </summary>
        private static void ConsolidateWeakSignals()
        {
            try
            {
                int promoted = 0;
                int pruned = 0;

                // Promote bandit entries that have enough samples
                var confirmedExamples = DynamicFewShotSelector.GetConfirmedExamples(minUseCount: 2);
                foreach (var ex in confirmedExamples)
                {
                    if (ex.UseCount >= 3 && ex.UseCount <= 5)
                    {
                        // Promote to adaptive weights
                        var ctx = PromptAnalyzer.Analyze(ex.Prompt);
                        var keyword = ExtractMainKeyword(ex.Prompt);
                        AdaptiveWeightManager.RecordSuccess(
                            ctx.PrimaryIntent.ToString(), keyword, ex.ToolName);
                        promoted++;
                    }
                }

                lock (_lock)
                {
                    _stats.NewSinceLastConsolidate = 0;
                    _stats.PatternsPromoted += promoted;
                    _stats.PatternsPruned += pruned;
                }

                if (promoted > 0)
                {
                    LearningEvent?.Invoke($"Consolidated {promoted} patterns into adaptive weights");
                    Debug.WriteLine($"[SelfLearning] Consolidated {promoted} promoted, {pruned} pruned");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelfLearning] Consolidate: {ex.Message}");
            }
        }

        /// <summary>
        /// Prepare and export training data when retraining threshold is reached.
        /// </summary>
        private static void PrepareRetrainingData()
        {
            try
            {
                if (string.IsNullOrEmpty(_dllDirectory)) return;

                var exportDir = Path.Combine(_dllDirectory, "Data", "Feedback");
                var exportPath = Path.Combine(exportDir, "training_export_auto.json");

                int exported = InteractionLogger.ExportTrainingBatch(exportPath);

                lock (_lock)
                {
                    _stats.NewSinceLastRetrain = 0;
                    _stats.RetrainNotified = false;
                    _stats.LastExportTime = DateTime.UtcNow.ToString("o");
                    _stats.LastExportCount = exported;
                }

                if (exported > 0)
                {
                    LearningEvent?.Invoke(
                        $"Exported {exported} samples for ANN retraining to {exportPath}");
                    Debug.WriteLine(
                        $"[SelfLearning] Exported {exported} training samples for retraining");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SelfLearning] PrepareRetrain: {ex.Message}");
            }
        }

        /// <summary>
        /// Get current learning statistics for display in UI.
        /// </summary>
        public static LearningStats GetStats()
        {
            lock (_lock) { return _stats.Clone(); }
        }

        /// <summary>
        /// Get a human-readable learning status summary.
        /// </summary>
        public static string GetStatusSummary()
        {
            lock (_lock)
            {
                var total = _stats.TotalInteractions;
                if (total == 0) return "";

                var successRate = _stats.SuccessfulInteractions * 100.0 / total;
                var parts = new List<string>
                {
                    $"{total} interactions ({successRate:F0}% success)"
                };

                if (_stats.PatternsPromoted > 0)
                    parts.Add($"{_stats.PatternsPromoted} learned patterns");

                if (_stats.LastExportCount > 0)
                    parts.Add($"{_stats.LastExportCount} training samples ready");

                return string.Join(", ", parts);
            }
        }

        private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","of","in","on","to","for","and","or","is","are","all","my","me","it",
            "this","that","with","by","from","at","please","can","how","what","show","list",
            "toi","cua","cac","va","cho","trong","la","co","khong","voi","hay","giup"
        };

        private static string ExtractMainKeyword(string prompt)
        {
            var words = prompt.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words
                .Where(w => w.Length > 2 && !_stopWords.Contains(w))
                .OrderByDescending(w => w.Length)
                .FirstOrDefault() ?? "";
        }

        #region Persistence

        private static DateTime _lastStatsSave = DateTime.MinValue;

        private static void SaveStatsDebounced()
        {
            if ((DateTime.UtcNow - _lastStatsSave).TotalSeconds < 30) return;
            _lastStatsSave = DateTime.UtcNow;
            SaveStats();
        }

        private static void SaveStats()
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_statsPath)) return;
                    var json = JsonSerializer.Serialize(_stats, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    File.WriteAllText(_statsPath, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SelfLearning] SaveStats: {ex.Message}");
                }
            }
        }

        private static void LoadStats()
        {
            lock (_lock)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_statsPath) && File.Exists(_statsPath))
                    {
                        var json = File.ReadAllText(_statsPath);
                        _stats = JsonSerializer.Deserialize<LearningStats>(json) ?? new();
                    }
                }
                catch
                {
                    _stats = new();
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

    /// <summary>
    /// Outcome of a single chatbot interaction, used as input to the learning pipeline.
    /// </summary>
    public class InteractionOutcome
    {
        public string Prompt { get; set; }
        public string Intent { get; set; }
        public string Category { get; set; }
        public string Keyword { get; set; }
        public string BanditStateKey { get; set; }
        public List<string> ToolNames { get; set; } = new();
        public List<ToolCallInfo> ToolCalls { get; set; } = new();
        public bool Success { get; set; }
        public bool WasCorrection { get; set; }
    }

    public class ToolCallInfo
    {
        public string Name { get; set; }
        public Dictionary<string, object> Args { get; set; } = new();
    }

    /// <summary>
    /// Persistent statistics for the self-learning system.
    /// </summary>
    public class LearningStats
    {
        [JsonPropertyName("total_interactions")] public int TotalInteractions { get; set; }
        [JsonPropertyName("successful")] public int SuccessfulInteractions { get; set; }
        [JsonPropertyName("failed")] public int FailedInteractions { get; set; }
        [JsonPropertyName("thumbs_up")] public int ThumbsUpCount { get; set; }
        [JsonPropertyName("thumbs_down")] public int ThumbsDownCount { get; set; }
        [JsonPropertyName("patterns_promoted")] public int PatternsPromoted { get; set; }
        [JsonPropertyName("patterns_pruned")] public int PatternsPruned { get; set; }
        [JsonPropertyName("new_since_augment")] public int NewSinceLastAugment { get; set; }
        [JsonPropertyName("new_since_retrain")] public int NewSinceLastRetrain { get; set; }
        [JsonPropertyName("new_since_consolidate")] public int NewSinceLastConsolidate { get; set; }
        [JsonPropertyName("retrain_notified")] public bool RetrainNotified { get; set; }
        [JsonPropertyName("last_interaction")] public string LastInteractionTime { get; set; }
        [JsonPropertyName("last_export")] public string LastExportTime { get; set; }
        [JsonPropertyName("last_export_count")] public int LastExportCount { get; set; }

        public LearningStats Clone() => (LearningStats)MemberwiseClone();
    }
}
