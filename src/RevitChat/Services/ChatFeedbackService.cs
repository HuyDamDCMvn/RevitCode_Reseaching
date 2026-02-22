using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitChat.Services
{
    public class ApprovedExample
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("tools")] public List<ToolUsage> Tools { get; set; } = new();
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
        [JsonPropertyName("useCount")] public int UseCount { get; set; }
    }

    public class ToolUsage
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; } = new();
    }

    public class CorrectionEntry
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("wrong_tool")] public string WrongTool { get; set; }
        [JsonPropertyName("correct_tool")] public string CorrectTool { get; set; }
        [JsonPropertyName("timestamp")] public string Timestamp { get; set; }
        [JsonPropertyName("hitCount")] public int HitCount { get; set; }
    }

    public class FeedbackData
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("approved")] public List<ApprovedExample> Approved { get; set; } = new();
        [JsonPropertyName("corrections")] public List<CorrectionEntry> Corrections { get; set; } = new();
    }

    public static class ChatFeedbackService
    {
        private static readonly object _lock = new();
        private static FeedbackData _data;
        private static string _filePath;
        private const int MaxApproved = 500;
        private const int MaxCorrections = 200;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string _userName;

        public static void Initialize(string dllDirectory)
        {
            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);

            _userName = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(dir, $"chat_feedback_{_userName}.json");
            Load();
        }

        public static string CurrentUser => _userName ?? "unknown";

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "default";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.ToLowerInvariant();
        }

        private static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        var json = File.ReadAllText(_filePath);
                        _data = JsonSerializer.Deserialize<FeedbackData>(json) ?? new FeedbackData();
                        return;
                    }
                }
                catch { }
                _data = new FeedbackData();
            }
        }

        private static void Save()
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_filePath)) return;
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOpts));
                }
                catch { }
            }
        }

        public static void SaveApproved(string prompt, List<ToolUsage> tools)
        {
            if (string.IsNullOrWhiteSpace(prompt) || tools == null || tools.Count == 0) return;

            lock (_lock)
            {
                EnsureLoaded();
                var existing = _data.Approved.FirstOrDefault(a =>
                    PromptSimilarity(a.Prompt, prompt) > 0.85);

                if (existing != null)
                {
                    existing.UseCount++;
                    existing.Tools = tools;
                    existing.Timestamp = DateTime.UtcNow.ToString("o");
                }
                else
                {
                    _data.Approved.Add(new ApprovedExample
                    {
                        Prompt = prompt,
                        Tools = tools,
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        UseCount = 1
                    });

                    if (_data.Approved.Count > MaxApproved)
                        _data.Approved.RemoveAt(0);
                }
                Save();
            }
        }

        public static void SaveCorrection(string prompt, string wrongTool, string correctTool)
        {
            if (string.IsNullOrWhiteSpace(wrongTool)) return;

            lock (_lock)
            {
                EnsureLoaded();
                var existing = _data.Corrections.FirstOrDefault(c =>
                    c.WrongTool == wrongTool && c.CorrectTool == correctTool);

                if (existing != null)
                {
                    existing.HitCount++;
                    existing.Timestamp = DateTime.UtcNow.ToString("o");
                }
                else
                {
                    _data.Corrections.Add(new CorrectionEntry
                    {
                        Prompt = prompt ?? "",
                        WrongTool = wrongTool,
                        CorrectTool = correctTool ?? "",
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        HitCount = 1
                    });

                    if (_data.Corrections.Count > MaxCorrections)
                        _data.Corrections.RemoveAt(0);
                }
                Save();
            }
        }

        /// <summary>
        /// Find approved examples similar to the given prompt (for few-shot injection).
        /// </summary>
        public static List<ApprovedExample> GetSimilarApproved(string prompt, int topK = 3)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return new();

            lock (_lock)
            {
                EnsureLoaded();
                return _data.Approved
                    .Select(a => (example: a, score: PromptSimilarity(a.Prompt, prompt)))
                    .Where(x => x.score > 0.25)
                    .OrderByDescending(x => x.score)
                    .ThenByDescending(x => x.example.UseCount)
                    .Take(topK)
                    .Select(x => x.example)
                    .ToList();
            }
        }

        /// <summary>
        /// Check if a tool name has a known correction (Option 2: override table).
        /// Returns the correct tool name, or null if no correction found.
        /// </summary>
        public static string FindToolCorrection(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                var correction = _data.Corrections
                    .Where(c => string.Equals(c.WrongTool, toolName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.HitCount)
                    .FirstOrDefault();

                if (correction != null && !string.IsNullOrEmpty(correction.CorrectTool))
                {
                    correction.HitCount++;
                    Save();
                    return correction.CorrectTool;
                }
                return null;
            }
        }

        /// <summary>
        /// Check if there's a direct prompt-to-tool override for frequently corrected patterns.
        /// Returns (toolName, args) if a high-confidence override exists.
        /// </summary>
        public static (string tool, Dictionary<string, object> args)? FindPromptOverride(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return null;

            lock (_lock)
            {
                EnsureLoaded();
                var match = _data.Approved
                    .Where(a => a.UseCount >= 3 && PromptSimilarity(a.Prompt, prompt) > 0.8)
                    .OrderByDescending(a => a.UseCount)
                    .FirstOrDefault();

                if (match?.Tools?.Count > 0)
                {
                    var tool = match.Tools[0];
                    return (tool.Name, tool.Args);
                }
                return null;
            }
        }

        public static int ApprovedCount
        {
            get { lock (_lock) { EnsureLoaded(); return _data.Approved.Count; } }
        }

        public static int CorrectionCount
        {
            get { lock (_lock) { EnsureLoaded(); return _data.Corrections.Count; } }
        }

        private static void EnsureLoaded()
        {
            if (_data == null) Load();
        }

        /// <summary>
        /// Keyword-overlap similarity (Jaccard) between two prompts.
        /// </summary>
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
            "was", "were", "be", "been", "this", "that", "it", "all", "my", "me",
            "tôi", "của", "các", "và", "cho", "trong", "là", "có", "không", "với"
        };

        private static HashSet<string> Tokenize(string text)
        {
            var words = text.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '[', ']', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);

            return new HashSet<string>(
                words.Where(w => w.Length > 1 && !StopWords.Contains(w)),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
