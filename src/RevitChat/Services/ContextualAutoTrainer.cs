using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitChat.Services
{
    /// <summary>
    /// Learns from failures and follow-up patterns at runtime.
    /// Auto-expands few-shot examples, normalization rules, and keyword groups
    /// based on observed interaction patterns.
    ///
    /// Covers gaps that static JSON can't: follow-up context, language issues,
    /// fallback-then-correction sequences, and novel prompt→tool mappings.
    /// </summary>
    public static class ContextualAutoTrainer
    {
        private static string _dataDir;
        private static readonly object _lock = new();
        private static LearnedContextData _data = new();
        private static string _filePath;
        private static string _previousUserPrompt = "";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Initialize(string dllDirectory)
        {
            _dataDir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(_dataDir);
            var user = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(_dataDir, $"contextual_learned_{user}.json");
            Load();
        }

        #region Track Prompt Flow

        public static void SetPreviousPrompt(string prompt)
        {
            if (!string.IsNullOrWhiteSpace(prompt))
                _previousUserPrompt = prompt;
        }

        public static string GetPreviousPrompt() => _previousUserPrompt;

        #endregion

        #region Record Failures

        /// <summary>
        /// Record when bot returned a fallback message (no tool call produced).
        /// This is the most important learning signal — prompts that the system couldn't handle.
        /// </summary>
        public static void RecordFallback(string prompt, string responseText, string previousPrompt = null)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            lock (_lock)
            {
                var existing = _data.FailedPrompts.FirstOrDefault(f =>
                    NormalizedEquals(f.Prompt, prompt));

                if (existing != null)
                {
                    existing.FailCount++;
                    existing.LastSeen = DateTime.UtcNow.ToString("o");
                }
                else
                {
                    _data.FailedPrompts.Add(new FailedPromptEntry
                    {
                        Prompt = prompt,
                        PreviousPrompt = previousPrompt ?? _previousUserPrompt,
                        ResponseSnippet = Truncate(responseText, 200),
                        FailCount = 1,
                        FirstSeen = DateTime.UtcNow.ToString("o"),
                        LastSeen = DateTime.UtcNow.ToString("o"),
                        IsFollowUp = !string.IsNullOrWhiteSpace(previousPrompt ?? _previousUserPrompt)
                    });

                    TrimFailedPrompts();
                }

                SaveDebounced();
            }
        }

        /// <summary>
        /// Record when the response contained Chinese characters (language issue).
        /// </summary>
        public static void RecordLanguageIssue(string prompt, string detectedLanguage)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;

            lock (_lock)
            {
                _data.LanguageIssueCount++;
                _data.LastLanguageIssue = DateTime.UtcNow.ToString("o");

                var existing = _data.FailedPrompts.FirstOrDefault(f =>
                    NormalizedEquals(f.Prompt, prompt));
                if (existing != null)
                    existing.HadLanguageIssue = true;

                SaveDebounced();
            }
        }

        #endregion

        #region Record Successes & Learn

        /// <summary>
        /// Record a successful tool call. If this prompt previously failed,
        /// auto-generate a learned few-shot example.
        /// </summary>
        public static void RecordSuccess(string prompt, string toolName,
            Dictionary<string, object> args, string previousPrompt = null)
        {
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(toolName)) return;

            lock (_lock)
            {
                var prev = previousPrompt ?? _previousUserPrompt;
                bool wasFollowUp = !string.IsNullOrWhiteSpace(prev);

                // Check if this was a previously failed prompt
                var failedEntry = _data.FailedPrompts.FirstOrDefault(f =>
                    NormalizedEquals(f.Prompt, prompt));

                if (failedEntry != null)
                {
                    failedEntry.ResolvedTool = toolName;
                    failedEntry.ResolvedAt = DateTime.UtcNow.ToString("o");

                    // Auto-generate few-shot from resolved failure
                    AutoLearnFewShot(prompt, toolName, args, wasFollowUp, prev);
                    _data.AutoLearnedCount++;
                }

                // Learn follow-up patterns
                if (wasFollowUp)
                    LearnFollowUpPattern(prev, prompt, toolName, args);

                // Learn normalization from successful prompts
                AutoLearnNormalization(prompt, toolName);

                SaveDebounced();
            }
        }

        /// <summary>
        /// Record when user corrects the bot (retry with different prompt).
        /// The correction pair (failed prompt → corrected prompt) is a strong learning signal.
        /// </summary>
        public static void RecordCorrection(string failedPrompt, string correctedPrompt,
            string toolName, Dictionary<string, object> args)
        {
            if (string.IsNullOrWhiteSpace(failedPrompt) || string.IsNullOrWhiteSpace(toolName)) return;

            lock (_lock)
            {
                _data.CorrectionPairs.Add(new CorrectionPair
                {
                    OriginalPrompt = failedPrompt,
                    CorrectedPrompt = correctedPrompt ?? failedPrompt,
                    CorrectTool = toolName,
                    Args = SimplifyArgs(args),
                    Timestamp = DateTime.UtcNow.ToString("o")
                });

                TrimCorrectionPairs();

                // The corrected version becomes a learned few-shot
                AutoLearnFewShot(failedPrompt, toolName, args, false, null);
                if (!string.IsNullOrWhiteSpace(correctedPrompt) && correctedPrompt != failedPrompt)
                    AutoLearnFewShot(correctedPrompt, toolName, args, false, null);

                _data.AutoLearnedCount++;
                SaveDebounced();
            }
        }

        #endregion

        #region Auto-Learn Methods

        private static void AutoLearnFewShot(string prompt, string toolName,
            Dictionary<string, object> args, bool isFollowUp, string previousPrompt)
        {
            var existing = _data.LearnedFewShots.FirstOrDefault(f =>
                NormalizedEquals(f.Prompt, prompt) && f.ToolName == toolName);

            if (existing != null)
            {
                existing.UseCount++;
                existing.LastUsed = DateTime.UtcNow.ToString("o");
                return;
            }

            var entry = new LearnedFewShotEntry
            {
                Prompt = prompt,
                ToolName = toolName,
                Args = SimplifyArgs(args),
                IsFollowUp = isFollowUp,
                PreviousPromptHint = isFollowUp ? ExtractSubject(previousPrompt) : null,
                UseCount = 1,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                LastUsed = DateTime.UtcNow.ToString("o")
            };

            _data.LearnedFewShots.Add(entry);

            // Also learn no-diacritics variant
            var stripped = PromptAnalyzer.StripDiacriticsPublic(prompt);
            if (stripped != prompt.ToLowerInvariant())
            {
                _data.LearnedFewShots.Add(new LearnedFewShotEntry
                {
                    Prompt = stripped,
                    ToolName = toolName,
                    Args = SimplifyArgs(args),
                    IsFollowUp = isFollowUp,
                    PreviousPromptHint = entry.PreviousPromptHint,
                    UseCount = 1,
                    CreatedAt = entry.CreatedAt,
                    LastUsed = entry.LastUsed
                });
            }

            TrimLearnedFewShots();
        }

        private static void LearnFollowUpPattern(string previousPrompt, string followUpPrompt,
            string toolName, Dictionary<string, object> args)
        {
            var subject = ExtractSubject(previousPrompt);
            if (string.IsNullOrWhiteSpace(subject)) return;

            var existing = _data.FollowUpPatterns.FirstOrDefault(f =>
                NormalizedEquals(f.FollowUpPrompt, followUpPrompt));

            if (existing != null)
            {
                existing.HitCount++;
                existing.LastTool = toolName;
                return;
            }

            _data.FollowUpPatterns.Add(new FollowUpPattern
            {
                SubjectHint = subject,
                FollowUpPrompt = followUpPrompt,
                LastTool = toolName,
                HitCount = 1,
                Timestamp = DateTime.UtcNow.ToString("o")
            });

            TrimFollowUpPatterns();
        }

        private static void AutoLearnNormalization(string prompt, string toolName)
        {
            var stripped = PromptAnalyzer.StripDiacriticsPublic(prompt);
            if (stripped == prompt.ToLowerInvariant()) return;

            var words = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word.Length < 3 || _commonWords.Contains(word)) continue;

                if (!_data.LearnedNormalizations.ContainsKey(word))
                {
                    var english = MapVietnameseToEnglish(word, toolName);
                    if (!string.IsNullOrEmpty(english))
                        _data.LearnedNormalizations[word] = english;
                }
            }
        }

        #endregion

        #region Query Methods (for prompt pipeline injection)

        /// <summary>
        /// Get learned few-shot examples relevant to the given prompt.
        /// These are auto-generated from past failures that were later resolved.
        /// </summary>
        public static List<string> GetLearnedExamples(string prompt, int topK = 3)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return new();

            lock (_lock)
            {
                return _data.LearnedFewShots
                    .Where(f => f.UseCount >= 1)
                    .Select(f => (entry: f, score: PromptSimilarity(f.Prompt, prompt)))
                    .Where(x => x.score > 0.2)
                    .OrderByDescending(x => x.score * (1 + x.entry.UseCount * 0.1))
                    .Take(topK)
                    .Select(x => FormatFewShot(x.entry))
                    .ToList();
            }
        }

        /// <summary>
        /// Get follow-up tool suggestion based on learned patterns.
        /// Returns (toolName, confidence) or null if no pattern matches.
        /// </summary>
        public static (string tool, double confidence)? GetFollowUpSuggestion(
            string currentPrompt, string previousPrompt)
        {
            if (string.IsNullOrWhiteSpace(currentPrompt)) return null;

            lock (_lock)
            {
                var match = _data.FollowUpPatterns
                    .Where(f => f.HitCount >= 2)
                    .Select(f => (pattern: f, score: PromptSimilarity(f.FollowUpPrompt, currentPrompt)))
                    .Where(x => x.score > 0.4)
                    .OrderByDescending(x => x.score * x.pattern.HitCount)
                    .FirstOrDefault();

                if (match.pattern != null)
                    return (match.pattern.LastTool, match.score);

                return null;
            }
        }

        /// <summary>
        /// Get learned normalization mappings to supplement static chat_normalization.json.
        /// </summary>
        public static Dictionary<string, string> GetLearnedNormalizations()
        {
            lock (_lock)
            {
                return new Dictionary<string, string>(_data.LearnedNormalizations);
            }
        }

        /// <summary>
        /// Get frequently failed prompts that haven't been resolved.
        /// Useful for diagnostics and manual review.
        /// </summary>
        public static List<FailedPromptEntry> GetUnresolvedFailures(int minFailCount = 2)
        {
            lock (_lock)
            {
                return _data.FailedPrompts
                    .Where(f => f.ResolvedTool == null && f.FailCount >= minFailCount)
                    .OrderByDescending(f => f.FailCount)
                    .Take(20)
                    .ToList();
            }
        }

        /// <summary>
        /// Get a summary of what the auto-trainer has learned.
        /// </summary>
        public static string GetSummary()
        {
            lock (_lock)
            {
                var parts = new List<string>();
                if (_data.LearnedFewShots.Count > 0)
                    parts.Add($"{_data.LearnedFewShots.Count} auto-learned examples");
                if (_data.FollowUpPatterns.Count > 0)
                    parts.Add($"{_data.FollowUpPatterns.Count} follow-up patterns");
                if (_data.LearnedNormalizations.Count > 0)
                    parts.Add($"{_data.LearnedNormalizations.Count} new normalizations");
                if (_data.FailedPrompts.Count(f => f.ResolvedTool == null) > 0)
                    parts.Add($"{_data.FailedPrompts.Count(f => f.ResolvedTool == null)} unresolved failures");
                if (_data.LanguageIssueCount > 0)
                    parts.Add($"{_data.LanguageIssueCount} language issues");

                return parts.Count > 0 ? string.Join(", ", parts) : "";
            }
        }

        #endregion

        #region Helpers

        private static string ExtractSubject(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return null;

            var lower = prompt.ToLowerInvariant();
            var subjectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"duct", "Ducts"}, {"ống gió", "Ducts"}, {"ong gio", "Ducts"},
                {"pipe", "Pipes"}, {"ống nước", "Pipes"}, {"ong nuoc", "Pipes"},
                {"conduit", "Conduits"}, {"ống dẫn", "Conduits"},
                {"cable tray", "CableTrays"}, {"máng cáp", "CableTrays"},
                {"wall", "Walls"}, {"tường", "Walls"},
                {"door", "Doors"}, {"cửa", "Doors"},
                {"room", "Rooms"}, {"phòng", "Rooms"},
                {"floor", "Floors"}, {"sàn", "Floors"},
                {"fitting", "Fittings"}, {"phụ kiện", "Fittings"},
            };

            foreach (var kvp in subjectMap)
            {
                if (lower.Contains(kvp.Key))
                    return kvp.Value;
            }

            return null;
        }

        private static string MapVietnameseToEnglish(string word, string toolName)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"ong", "pipe"}, {"gio", "duct"}, {"nuoc", "water"},
                {"noi", "connect"}, {"cat", "split"}, {"dem", "count"},
                {"kiem", "check"}, {"tra", "check"}, {"tim", "find"},
                {"xoa", "delete"}, {"an", "hide"}, {"hien", "show"},
                {"mau", "color"}, {"xuat", "export"}, {"tao", "create"},
                {"phan", "classify"}, {"loai", "type"}, {"size", "size"},
                {"level", "level"}, {"tang", "level"},
            };

            return map.TryGetValue(word, out var eng) ? eng : null;
        }

        private static string FormatFewShot(LearnedFewShotEntry entry)
        {
            var argsStr = entry.Args.Count > 0
                ? JsonSerializer.Serialize(entry.Args)
                : "{}";
            var prefix = entry.IsFollowUp && !string.IsNullOrEmpty(entry.PreviousPromptHint)
                ? $"(follow-up about {entry.PreviousPromptHint}) "
                : "";
            return $"{prefix}User: {entry.Prompt}\n<tool_call>\n{{\"name\": \"{entry.ToolName}\", \"arguments\": {argsStr}}}\n</tool_call>";
        }

        private static Dictionary<string, object> SimplifyArgs(Dictionary<string, object> args)
        {
            if (args == null) return new();
            var result = new Dictionary<string, object>();
            foreach (var kvp in args)
            {
                if (kvp.Value is JsonElement je)
                {
                    result[kvp.Key] = je.ValueKind switch
                    {
                        JsonValueKind.String => je.GetString(),
                        JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => je.GetRawText()
                    };
                }
                else
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
            return result;
        }

        private static bool NormalizedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            return PromptAnalyzer.StripDiacriticsPublic(a.Trim())
                .Equals(PromptAnalyzer.StripDiacriticsPublic(b.Trim()), StringComparison.OrdinalIgnoreCase);
        }

        private static double PromptSimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
            var setA = Tokenize(a);
            var setB = Tokenize(b);
            if (setA.Count == 0 || setB.Count == 0) return 0;
            int intersection = setA.Count(t => setB.Contains(t));
            int union = setA.Union(setB).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }

        private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","of","in","on","to","for","and","or","is","are",
            "toi","cua","cac","va","cho","trong","la","co","khong","voi","hay","giup",
            "please","help","can","how","what","show","list","get"
        };

        private static readonly HashSet<string> _commonWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","of","in","on","to","for","and","or","is","are",
            "toi","cua","cac","va","cho","trong","la","co","khong","voi",
            "hay","giup","nay","do","dang","duoc","da","se","bi","bao","nhieu"
        };

        private static HashSet<string> Tokenize(string text)
        {
            var words = text.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '"', '(', ')', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
            return new HashSet<string>(
                words.Where(w => w.Length > 1 && !_stopWords.Contains(w)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLen ? text : text[..maxLen] + "...";
        }

        private static void TrimFailedPrompts()
        {
            if (_data.FailedPrompts.Count > 500)
                _data.FailedPrompts = _data.FailedPrompts
                    .OrderByDescending(f => f.FailCount)
                    .Take(400).ToList();
        }

        private static void TrimLearnedFewShots()
        {
            if (_data.LearnedFewShots.Count > 500)
                _data.LearnedFewShots = _data.LearnedFewShots
                    .OrderByDescending(f => f.UseCount)
                    .Take(400).ToList();
        }

        private static void TrimFollowUpPatterns()
        {
            if (_data.FollowUpPatterns.Count > 200)
                _data.FollowUpPatterns = _data.FollowUpPatterns
                    .OrderByDescending(f => f.HitCount)
                    .Take(150).ToList();
        }

        private static void TrimCorrectionPairs()
        {
            if (_data.CorrectionPairs.Count > 300)
                _data.CorrectionPairs = _data.CorrectionPairs
                    .Skip(_data.CorrectionPairs.Count - 250).ToList();
        }

        #endregion

        #region Persistence

        private static DateTime _lastSave = DateTime.MinValue;

        private static void SaveDebounced()
        {
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < 15) return;
            _lastSave = DateTime.UtcNow;
            Save();
        }

        internal static void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath)) return;
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOpts));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContextualAutoTrainer] Save: {ex.Message}");
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
                        _data = JsonSerializer.Deserialize<LearnedContextData>(json) ?? new();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ContextualAutoTrainer] Load: {ex.Message}");
                    _data = new();
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

    #region Data Models

    public class LearnedContextData
    {
        [JsonPropertyName("failed_prompts")] public List<FailedPromptEntry> FailedPrompts { get; set; } = new();
        [JsonPropertyName("learned_fewshots")] public List<LearnedFewShotEntry> LearnedFewShots { get; set; } = new();
        [JsonPropertyName("followup_patterns")] public List<FollowUpPattern> FollowUpPatterns { get; set; } = new();
        [JsonPropertyName("correction_pairs")] public List<CorrectionPair> CorrectionPairs { get; set; } = new();
        [JsonPropertyName("learned_norms")] public Dictionary<string, string> LearnedNormalizations { get; set; } = new();
        [JsonPropertyName("auto_learned_count")] public int AutoLearnedCount { get; set; }
        [JsonPropertyName("language_issue_count")] public int LanguageIssueCount { get; set; }
        [JsonPropertyName("last_language_issue")] public string LastLanguageIssue { get; set; }
    }

    public class FailedPromptEntry
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("prev_prompt")] public string PreviousPrompt { get; set; }
        [JsonPropertyName("response_snippet")] public string ResponseSnippet { get; set; }
        [JsonPropertyName("fail_count")] public int FailCount { get; set; }
        [JsonPropertyName("first_seen")] public string FirstSeen { get; set; }
        [JsonPropertyName("last_seen")] public string LastSeen { get; set; }
        [JsonPropertyName("is_followup")] public bool IsFollowUp { get; set; }
        [JsonPropertyName("had_language_issue")] public bool HadLanguageIssue { get; set; }
        [JsonPropertyName("resolved_tool")] public string ResolvedTool { get; set; }
        [JsonPropertyName("resolved_at")] public string ResolvedAt { get; set; }
    }

    public class LearnedFewShotEntry
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("tool")] public string ToolName { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; } = new();
        [JsonPropertyName("is_followup")] public bool IsFollowUp { get; set; }
        [JsonPropertyName("prev_hint")] public string PreviousPromptHint { get; set; }
        [JsonPropertyName("use_count")] public int UseCount { get; set; }
        [JsonPropertyName("created")] public string CreatedAt { get; set; }
        [JsonPropertyName("last_used")] public string LastUsed { get; set; }
    }

    public class FollowUpPattern
    {
        [JsonPropertyName("subject")] public string SubjectHint { get; set; }
        [JsonPropertyName("followup")] public string FollowUpPrompt { get; set; }
        [JsonPropertyName("tool")] public string LastTool { get; set; }
        [JsonPropertyName("hits")] public int HitCount { get; set; }
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
    }

    public class CorrectionPair
    {
        [JsonPropertyName("original")] public string OriginalPrompt { get; set; }
        [JsonPropertyName("corrected")] public string CorrectedPrompt { get; set; }
        [JsonPropertyName("tool")] public string CorrectTool { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; } = new();
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
    }

    #endregion
}
