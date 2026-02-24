using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitChat.Services
{
    /// <summary>
    /// Mines successful interactions to auto-generate few-shot examples.
    /// Provides dynamic, project-aware examples alongside static ones.
    /// </summary>
    public static class DynamicFewShotSelector
    {
        private static readonly object _lock = new();
        private static List<LearnedExample> _examples = new();
        private static string _filePath;
        private const int MaxExamples = 300;
        private const int MinUseCount = 2;
        private const double MinSimilarityToMerge = 0.85;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Initialize(string dllDirectory)
        {
            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);
            var user = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(dir, $"learned_examples_{user}.json");
            Load();
        }

        /// <summary>
        /// Record a successful interaction as a potential few-shot example.
        /// Only records when the tool call succeeded and returned meaningful results.
        /// </summary>
        public static void RecordSuccess(string prompt, string toolName,
            Dictionary<string, object> args, string intent, string category)
        {
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(toolName)) return;

            lock (_lock)
            {
                var existing = _examples.FirstOrDefault(e =>
                    PromptSimilarity(e.Prompt, prompt) > MinSimilarityToMerge &&
                    e.ToolName == toolName);

                if (existing != null)
                {
                    existing.UseCount++;
                    existing.LastUsed = DateTime.UtcNow.ToString("o");
                    if (args != null && args.Count > existing.Args.Count)
                        existing.Args = SerializeArgs(args);
                }
                else
                {
                    _examples.Add(new LearnedExample
                    {
                        Prompt = prompt,
                        ToolName = toolName,
                        Args = SerializeArgs(args),
                        Intent = intent,
                        Category = category,
                        UseCount = 1,
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                        LastUsed = DateTime.UtcNow.ToString("o")
                    });

                    if (_examples.Count > MaxExamples)
                    {
                        _examples = _examples
                            .OrderByDescending(e => e.UseCount)
                            .ThenByDescending(e => e.LastUsed)
                            .Take(MaxExamples)
                            .ToList();
                    }
                }

                SaveDebounced();
            }
        }

        /// <summary>
        /// Get the most relevant learned examples for a given prompt.
        /// Combines keyword overlap similarity with usage frequency.
        /// </summary>
        public static List<string> GetDynamicExamples(string prompt, int topK = 3,
            string preferredIntent = null, string preferredCategory = null)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return new();

            lock (_lock)
            {
                var scored = _examples
                    .Where(e => e.UseCount >= MinUseCount)
                    .Select(e =>
                    {
                        double sim = PromptSimilarity(e.Prompt, prompt);
                        double bonus = 0;
                        if (preferredIntent != null && e.Intent == preferredIntent) bonus += 0.1;
                        if (preferredCategory != null && e.Category == preferredCategory) bonus += 0.1;
                        bonus += Math.Min(0.2, e.UseCount * 0.02);
                        return (example: e, score: sim + bonus);
                    })
                    .Where(x => x.score > 0.2)
                    .OrderByDescending(x => x.score)
                    .Take(topK)
                    .ToList();

                return scored.Select(s => FormatAsExample(s.example)).ToList();
            }
        }

        /// <summary>
        /// Get total learned example count.
        /// </summary>
        public static int ExampleCount
        {
            get { lock (_lock) { return _examples.Count; } }
        }

        /// <summary>
        /// Get examples that have been validated (high useCount, confirmed patterns).
        /// </summary>
        public static List<LearnedExample> GetConfirmedExamples(int minUseCount = 3)
        {
            lock (_lock)
            {
                return _examples
                    .Where(e => e.UseCount >= minUseCount)
                    .OrderByDescending(e => e.UseCount)
                    .ToList();
            }
        }

        private static string FormatAsExample(LearnedExample e)
        {
            var argsStr = e.Args.Count > 0
                ? JsonSerializer.Serialize(e.Args)
                : "{}";
            return $"User: {e.Prompt}\n<tool_call>\n{{\"name\": \"{e.ToolName}\", \"arguments\": {argsStr}}}\n</tool_call>";
        }

        private static Dictionary<string, object> SerializeArgs(Dictionary<string, object> args)
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

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "in", "on", "to", "for", "and", "or", "is", "are",
            "tôi", "của", "các", "và", "cho", "trong", "là", "có", "không", "với",
            "hãy", "hay", "xin", "vui", "lòng", "giúp", "please", "help"
        };

        private static HashSet<string> Tokenize(string text)
        {
            var words = text.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
            return new HashSet<string>(
                words.Where(w => w.Length > 1 && !StopWords.Contains(w)),
                StringComparer.OrdinalIgnoreCase);
        }

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
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_examples, JsonOpts));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DynamicFewShotSelector] Save: {ex.Message}");
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
                        _examples = JsonSerializer.Deserialize<List<LearnedExample>>(json) ?? new();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DynamicFewShotSelector] Load: {ex.Message}");
                    _examples = new();
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

    public class LearnedExample
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("tool")] public string ToolName { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; } = new();
        [JsonPropertyName("intent")] public string Intent { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; }
        [JsonPropertyName("use_count")] public int UseCount { get; set; }
        [JsonPropertyName("created")] public string CreatedAt { get; set; }
        [JsonPropertyName("last_used")] public string LastUsed { get; set; }
    }
}
