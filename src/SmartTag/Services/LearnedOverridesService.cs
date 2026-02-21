using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Stores and retrieves learned tag placement overrides from export data.
    /// File: Data/Training/learned_overrides.json (auto-updated when user exports from Revit).
    /// Does not modify hand-written Rules/Patterns.
    /// </summary>
    public class LearnedOverridesService
    {
        private static LearnedOverridesService _instance;
        private static readonly object _lock = new();

        private LearnedOverridesData _data;
        private string _filePath;
        private bool _loaded;
        private bool _annotatedIngested;

        public static LearnedOverridesService Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= new LearnedOverridesService(); }
                return _instance;
            }
        }

        private LearnedOverridesService()
        {
            _data = new LearnedOverridesData();
        }

        private string GetFilePath()
        {
            if (!string.IsNullOrEmpty(_filePath))
                return _filePath;

            _filePath = DataPathResolver.Resolve("Training/learned_overrides.json");
            return _filePath;
        }

        /// <summary>
        /// Load from disk (called automatically when needed).
        /// </summary>
        public void EnsureLoaded()
        {
            if (_loaded) return;

            lock (_lock)
            {
                if (_loaded) return;

                var path = GetFilePath();
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        _data = JsonSerializer.Deserialize<LearnedOverridesData>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LearnedOverridesData();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"LearnedOverridesService: Load failed {ex.Message}");
                    }
                }
                else
                {
                    _data = new LearnedOverridesData();
                }

                _data.ByCategory ??= new Dictionary<string, LearnedOverride>();
                _data.ByCategoryAndSystem ??= new Dictionary<string, LearnedOverride>();
                _loaded = true;
            }

            EnsureAnnotatedIngested();
        }

        /// <summary>
        /// Once per load: ingest all JSON in Data/Training/annotated (e.g. HD.extension\lib\net8\Data\Training\annotated)
        /// and merge into learned overrides so the tool uses that folder in its algorithm.
        /// </summary>
        private void EnsureAnnotatedIngested()
        {
            if (_annotatedIngested) return;

            lock (_lock)
            {
                if (_annotatedIngested) return;

                var annotatedPath = GetAnnotatedFolderPath();
                if (string.IsNullOrEmpty(annotatedPath) || !Directory.Exists(annotatedPath))
                {
                    _annotatedIngested = true;
                    return;
                }

                var fromAnnotated = new ExportIngestionService().AggregateFromFolder(annotatedPath);
                if (fromAnnotated != null)
                    MergeInIngestedData(fromAnnotated);

                _annotatedIngested = true;
            }
        }

        private string GetAnnotatedFolderPath()
        {
            var learnedPath = GetFilePath();
            var trainingDir = Path.GetDirectoryName(learnedPath);
            return string.IsNullOrEmpty(trainingDir) ? null : Path.Combine(trainingDir, "annotated");
        }

        private void MergeInIngestedData(LearnedOverridesData fromAnnotated)
        {
            if (fromAnnotated?.ByCategory != null)
            {
                foreach (var kv in fromAnnotated.ByCategory)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value?.SampleCount <= 0) continue;
                    if (_data.ByCategory.TryGetValue(kv.Key, out var existing))
                    {
                        if (kv.Value.SampleCount >= existing.SampleCount)
                            _data.ByCategory[kv.Key] = kv.Value;
                    }
                    else
                        _data.ByCategory[kv.Key] = kv.Value;
                }
            }
            if (fromAnnotated?.ByCategoryAndSystem != null)
            {
                foreach (var kv in fromAnnotated.ByCategoryAndSystem)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value?.SampleCount <= 0) continue;
                    if (_data.ByCategoryAndSystem.TryGetValue(kv.Key, out var existing))
                    {
                        if (kv.Value.SampleCount >= existing.SampleCount)
                            _data.ByCategoryAndSystem[kv.Key] = kv.Value;
                    }
                    else
                        _data.ByCategoryAndSystem[kv.Key] = kv.Value;
                }
            }
            _data.UpdatedAt = DateTime.UtcNow.ToString("o");
            Save();
        }

        /// <summary>
        /// Get preferred positions for category (and optional system). Returns null if no learned data.
        /// </summary>
        public List<string> GetPreferredPositions(string category, string systemName = null)
        {
            EnsureLoaded();

            if (!string.IsNullOrEmpty(systemName))
            {
                var key = $"{category}|{systemName}";
                if (_data.ByCategoryAndSystem.TryGetValue(key, out var o) && o.PreferredPositions?.Count > 0)
                    return new List<string>(o.PreferredPositions);
            }

            if (_data.ByCategory.TryGetValue(category ?? "", out var cat) && cat.PreferredPositions?.Count > 0)
                return new List<string>(cat.PreferredPositions);

            return null;
        }

        /// <summary>
        /// Get addLeader hint. Returns null if no learned data.
        /// </summary>
        public bool? GetAddLeader(string category, string systemName = null)
        {
            EnsureLoaded();

            if (!string.IsNullOrEmpty(systemName))
            {
                var key = $"{category}|{systemName}";
                if (_data.ByCategoryAndSystem.TryGetValue(key, out var o))
                    return o.AddLeader;
            }

            if (_data.ByCategory.TryGetValue(category ?? "", out var cat))
                return cat.AddLeader;

            return null;
        }

        /// <summary>
        /// Get alignment hints from learned data. Returns null if no learned data.
        /// </summary>
        public (bool? alignRow, bool? alignCol) GetPreferAlignment(string category, string systemName = null)
        {
            EnsureLoaded();
            LearnedOverride o = null;
            if (!string.IsNullOrEmpty(systemName))
            {
                var key = $"{category}|{systemName}";
                _data.ByCategoryAndSystem.TryGetValue(key, out o);
            }
            if (o == null)
                _data.ByCategory.TryGetValue(category ?? "", out o);
            if (o == null) return (null, null);
            return (o.PreferAlignRow, o.PreferAlignColumn);
        }

        /// <summary>
        /// Get offset distance hint (feet). Returns null if no learned data.
        /// </summary>
        public double? GetOffsetDistance(string category, string systemName = null)
        {
            EnsureLoaded();

            if (!string.IsNullOrEmpty(systemName))
            {
                var key = $"{category}|{systemName}";
                if (_data.ByCategoryAndSystem.TryGetValue(key, out var o) && o.OffsetDistance.HasValue)
                    return o.OffsetDistance;
            }

            if (_data.ByCategory.TryGetValue(category ?? "", out var cat) && cat.OffsetDistance.HasValue)
                return cat.OffsetDistance;

            return null;
        }

        /// <summary>
        /// Replace or merge learned overrides (called by ingestion after export).
        /// </summary>
        public void UpdateOverrides(LearnedOverridesData newData)
        {
            if (newData == null) return;

            lock (_lock)
            {
                EnsureLoaded();
                _data.UpdatedAt = DateTime.UtcNow.ToString("o");

                foreach (var kv in newData.ByCategory ?? new Dictionary<string, LearnedOverride>())
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value?.SampleCount <= 0) continue;
                    _data.ByCategory[kv.Key] = kv.Value;
                }
                foreach (var kv in newData.ByCategoryAndSystem ?? new Dictionary<string, LearnedOverride>())
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value?.SampleCount <= 0) continue;
                    _data.ByCategoryAndSystem[kv.Key] = kv.Value;
                }

                Save();
            }
        }

        /// <summary>
        /// Persist to disk.
        /// </summary>
        public void Save()
        {
            var path = GetFilePath();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };
                var json = JsonSerializer.Serialize(_data, options);
                File.WriteAllText(path, json);
                System.Diagnostics.Debug.WriteLine($"LearnedOverridesService: Saved to {path}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LearnedOverridesService: Save failed {ex.Message}");
            }
        }

        /// <summary>
        /// Clear learned overrides (for testing or reset).
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _data = new LearnedOverridesData
                {
                    ByCategory = new Dictionary<string, LearnedOverride>(),
                    ByCategoryAndSystem = new Dictionary<string, LearnedOverride>(),
                    UpdatedAt = DateTime.UtcNow.ToString("o")
                };
                Save();
            }
        }

        public int CategoryCount => _data?.ByCategory?.Count ?? 0;
        public int CategoryAndSystemCount => _data?.ByCategoryAndSystem?.Count ?? 0;
    }

    /// <summary>
    /// Learned override for one category (or category+system).
    /// </summary>
    public class LearnedOverride
    {
        [JsonPropertyName("preferredPositions")]
        public List<string> PreferredPositions { get; set; }

        [JsonPropertyName("addLeader")]
        public bool? AddLeader { get; set; }

        [JsonPropertyName("offsetDistance")]
        public double? OffsetDistance { get; set; }

        /// <summary>When true, majority of samples had tag aligned with other tags in row (same Y).</summary>
        [JsonPropertyName("preferAlignRow")]
        public bool? PreferAlignRow { get; set; }

        /// <summary>When true, majority of samples had tag aligned with other tags in column (same X).</summary>
        [JsonPropertyName("preferAlignColumn")]
        public bool? PreferAlignColumn { get; set; }

        [JsonPropertyName("sampleCount")]
        public int SampleCount { get; set; }
    }

    /// <summary>
    /// Root structure of learned_overrides.json.
    /// </summary>
    public class LearnedOverridesData
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("updatedAt")]
        public string UpdatedAt { get; set; }

        [JsonPropertyName("byCategory")]
        public Dictionary<string, LearnedOverride> ByCategory { get; set; } = new();

        [JsonPropertyName("byCategoryAndSystem")]
        public Dictionary<string, LearnedOverride> ByCategoryAndSystem { get; set; } = new();
    }
}
