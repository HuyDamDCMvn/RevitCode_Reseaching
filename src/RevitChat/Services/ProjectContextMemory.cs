using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitChat.Services
{
    /// <summary>
    /// Remembers tool/category/system usage patterns per Revit project.
    /// When "ống" is ambiguous, the project profile helps disambiguate
    /// (e.g., an MEP project will default to Ducts/Pipes based on past usage).
    /// </summary>
    public static class ProjectContextMemory
    {
        private static readonly object _lock = new();
        private static ProjectProfile _currentProfile;
        private static string _currentProjectKey;
        private static string _storageDir;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Initialize(string dllDirectory)
        {
            _storageDir = Path.Combine(dllDirectory, "Data", "Feedback", "Projects");
            Directory.CreateDirectory(_storageDir);
        }

        /// <summary>
        /// Load or create profile for the current project.
        /// </summary>
        public static void SetProject(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) projectName = "default";
            var key = SanitizeFileName(projectName);
            if (key == _currentProjectKey && _currentProfile != null) return;

            _currentProjectKey = key;
            Load(key);
        }

        /// <summary>
        /// Record that a tool was used in this project.
        /// </summary>
        public static void RecordToolUsage(string toolName, string category, string system, string intent)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return;
            lock (_lock)
            {
                if (_currentProfile == null) return;

                IncrementCounter(_currentProfile.ToolFrequency, toolName);
                if (!string.IsNullOrEmpty(category))
                    IncrementCounter(_currentProfile.CategoryFrequency, category);
                if (!string.IsNullOrEmpty(system))
                    IncrementCounter(_currentProfile.SystemFrequency, system);
                if (!string.IsNullOrEmpty(intent))
                    IncrementCounter(_currentProfile.IntentFrequency, intent);

                _currentProfile.TotalInteractions++;
                _currentProfile.LastUsed = DateTime.UtcNow.ToString("o");
                SaveDebounced();
            }
        }

        /// <summary>
        /// Get the most frequently used categories in this project, sorted by frequency.
        /// </summary>
        public static List<string> GetTopCategories(int topK = 5)
        {
            lock (_lock)
            {
                if (_currentProfile == null) return new();
                return _currentProfile.CategoryFrequency
                    .OrderByDescending(kv => kv.Value)
                    .Take(topK)
                    .Select(kv => kv.Key)
                    .ToList();
            }
        }

        /// <summary>
        /// Get the most frequently used tools in this project.
        /// </summary>
        public static List<string> GetTopTools(int topK = 10)
        {
            lock (_lock)
            {
                if (_currentProfile == null) return new();
                return _currentProfile.ToolFrequency
                    .OrderByDescending(kv => kv.Value)
                    .Take(topK)
                    .Select(kv => kv.Key)
                    .ToList();
            }
        }

        /// <summary>
        /// Disambiguate "ống" based on project history.
        /// Returns the most likely category (Ducts/Pipes) or null if uncertain.
        /// </summary>
        public static string DisambiguateByProjectHistory(string ambiguousKeyword)
        {
            lock (_lock)
            {
                if (_currentProfile == null) return null;

                var catFreq = _currentProfile.CategoryFrequency;
                int ductScore = GetCountSafe(catFreq, "Ducts") + GetCountSafe(catFreq, "Duct Fittings")
                    + GetCountSafe(catFreq, "Duct Accessories");
                int pipeScore = GetCountSafe(catFreq, "Pipes") + GetCountSafe(catFreq, "Pipe Fittings")
                    + GetCountSafe(catFreq, "Pipe Accessories");
                int conduitScore = GetCountSafe(catFreq, "Conduits");

                if (ductScore == 0 && pipeScore == 0 && conduitScore == 0)
                    return null;

                if (ductScore > pipeScore && ductScore > conduitScore)
                    return "Ducts";
                if (pipeScore > ductScore && pipeScore > conduitScore)
                    return "Pipes";
                if (conduitScore > ductScore && conduitScore > pipeScore)
                    return "Conduits";

                return null;
            }
        }

        /// <summary>
        /// Check if a specific tool is commonly used in this project (above average frequency).
        /// </summary>
        public static bool IsFrequentTool(string toolName)
        {
            lock (_lock)
            {
                if (_currentProfile == null || _currentProfile.ToolFrequency.Count == 0) return false;
                if (!_currentProfile.ToolFrequency.TryGetValue(toolName, out int count)) return false;
                double avg = _currentProfile.ToolFrequency.Values.Average();
                return count > avg;
            }
        }

        /// <summary>
        /// Get a brief project profile summary for context injection.
        /// </summary>
        public static string GetProfileSummary()
        {
            lock (_lock)
            {
                if (_currentProfile == null || _currentProfile.TotalInteractions < 5)
                    return null;

                var topCats = GetTopCategories(3);
                var topTools = GetTopTools(5);
                if (topCats.Count == 0) return null;

                return $"[Project Profile] Common categories: {string.Join(", ", topCats)}. " +
                       $"Frequent tools: {string.Join(", ", topTools)}.";
            }
        }

        private static int GetCountSafe(Dictionary<string, int> dict, string key)
        {
            return dict.TryGetValue(key, out int val) ? val : 0;
        }

        private static void IncrementCounter(Dictionary<string, int> dict, string key)
        {
            dict.TryGetValue(key, out int current);
            dict[key] = current + 1;
        }

        private static DateTime _lastSave = DateTime.MinValue;
        private static void SaveDebounced()
        {
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < 30) return;
            _lastSave = DateTime.UtcNow;
            Save();
        }

        internal static void Save()
        {
            lock (_lock)
            {
                try
                {
                    if (_currentProfile == null || string.IsNullOrEmpty(_storageDir)) return;
                    var path = Path.Combine(_storageDir, $"{_currentProjectKey}.json");
                    File.WriteAllText(path, JsonSerializer.Serialize(_currentProfile, JsonOpts));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProjectContextMemory] Save: {ex.Message}");
                }
            }
        }

        private static void Load(string key)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_storageDir))
                    {
                        _currentProfile = new ProjectProfile();
                        return;
                    }
                    var path = Path.Combine(_storageDir, $"{key}.json");
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        _currentProfile = JsonSerializer.Deserialize<ProjectProfile>(json) ?? new ProjectProfile();
                    }
                    else
                    {
                        _currentProfile = new ProjectProfile();
                    }
                    _currentProfile.ProjectKey = key;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ProjectContextMemory] Load: {ex.Message}");
                    _currentProfile = new ProjectProfile { ProjectKey = key };
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "default";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 60 ? name[..60] : name.ToLowerInvariant();
        }
    }

    public class ProjectProfile
    {
        [JsonPropertyName("project_key")] public string ProjectKey { get; set; }
        [JsonPropertyName("tool_freq")] public Dictionary<string, int> ToolFrequency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("category_freq")] public Dictionary<string, int> CategoryFrequency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("system_freq")] public Dictionary<string, int> SystemFrequency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("intent_freq")] public Dictionary<string, int> IntentFrequency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("total")] public int TotalInteractions { get; set; }
        [JsonPropertyName("last_used")] public string LastUsed { get; set; }
    }
}
