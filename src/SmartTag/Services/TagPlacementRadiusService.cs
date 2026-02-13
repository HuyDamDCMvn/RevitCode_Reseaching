using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmartTag.ML;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Learn a useful tag placement radius from annotated samples.
    /// Tags should stay within this radius from the element center.
    /// </summary>
    public class TagPlacementRadiusService
    {
        private static TagPlacementRadiusService _instance;
        private static readonly object _lock = new();

        private readonly List<TagPlacementRadiusEntry> _entries = new();
        private string _radiusPath;

        public static TagPlacementRadiusService Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= new TagPlacementRadiusService(); }
                return _instance;
            }
        }

        private TagPlacementRadiusService() { }

        public double GetRadius(TaggableElement element, ElementContext context, double fallbackRadius)
        {
            if (element == null) return fallbackRadius;
            EnsureLoaded();

            if (_entries.Count == 0) return fallbackRadius;

            var category = (element.BuiltInCategoryName ?? element.CategoryName ?? "").Trim();
            if (string.IsNullOrEmpty(category)) return fallbackRadius;

            var systemType = NormalizeSystemType(element.SystemClassification ?? element.SystemName ?? "");
            var density = ContextToDensity(context);
            var isLinear = element.IsLinearElement;

            TagPlacementRadiusEntry best = null;
            int bestScore = -1;

            foreach (var entry in _entries)
            {
                if (!entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                    continue;

                int score = 0;
                if (entry.IsLinear == isLinear) score += 2;
                if (!string.IsNullOrEmpty(systemType) &&
                    entry.SystemType.Equals(systemType, StringComparison.OrdinalIgnoreCase)) score += 4;
                else if (string.IsNullOrEmpty(entry.SystemType)) score += 1;

                if (entry.Density.Equals(density, StringComparison.OrdinalIgnoreCase)) score += 2;
                else if (string.IsNullOrEmpty(entry.Density)) score += 1;

                if (score > bestScore ||
                    (score == bestScore && (best == null || entry.SampleCount > best.SampleCount)))
                {
                    bestScore = score;
                    best = entry;
                }
            }

            if (best == null || best.Radius <= 0)
                return fallbackRadius;

            return best.Radius;
        }

        private void EnsureLoaded()
        {
            if (_entries.Count > 0) return;

            lock (_lock)
            {
                if (_entries.Count > 0) return;

                var path = GetRadiusPath();
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var file = JsonSerializer.Deserialize<TagPlacementRadiusFile>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (file?.Radii != null && file.Radii.Count > 0)
                        {
                            _entries.AddRange(file.Radii);
                            return;
                        }
                    }
                    catch { }
                }

                var annotatedPath = GetAnnotatedFolderPath();
                var built = BuildFromAnnotatedFolder(annotatedPath);
                if (built?.Radii != null && built.Radii.Count > 0)
                {
                    _entries.AddRange(built.Radii);
                    SaveToFile(path, built);
                }
            }
        }

        private string GetRadiusPath()
        {
            if (!string.IsNullOrEmpty(_radiusPath))
                return _radiusPath;

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "Data", "Training", "auto_radius.json"),
                Path.Combine(assemblyDir, "..", "Data", "Training", "auto_radius.json"),
                Path.Combine(assemblyDir, "..", "..", "src", "SmartTag", "Data", "Training", "auto_radius.json"),
                Path.Combine(Environment.CurrentDirectory, "Data", "Training", "auto_radius.json"),
                @"D:\03_DCMvn\RevitCode\src\SmartTag\Data\Training\auto_radius.json"
            };

            foreach (var path in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(full);
                    if (Directory.Exists(dir) || dir != null)
                    {
                        _radiusPath = full;
                        return full;
                    }
                }
                catch { }
            }

            _radiusPath = Path.Combine(Path.GetTempPath(), "SmartTag_auto_radius.json");
            return _radiusPath;
        }

        private string GetAnnotatedFolderPath()
        {
            var radiusPath = GetRadiusPath();
            var trainingDir = Path.GetDirectoryName(radiusPath);
            return string.IsNullOrEmpty(trainingDir) ? null : Path.Combine(trainingDir, "annotated");
        }

        private TagPlacementRadiusFile BuildFromAnnotatedFolder(string annotatedFolderPath)
        {
            var result = new TagPlacementRadiusFile
            {
                Version = "1.0",
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };

            if (string.IsNullOrEmpty(annotatedFolderPath) || !Directory.Exists(annotatedFolderPath))
                return result;

            var files = Directory.GetFiles(annotatedFolderPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0) return result;

            var buckets = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<TrainingDataFile>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (data?.Samples == null) continue;

                    foreach (var sample in data.Samples)
                    {
                        if (sample?.Element == null || sample.Tag == null) continue;

                        var category = (sample.Element.Category ?? "").Trim();
                        if (string.IsNullOrEmpty(category)) continue;

                        var systemType = NormalizeSystemType(sample.Element.SystemType ?? "");
                        var density = NormalizeDensity(sample.Context?.Density);
                        var isLinear = sample.Element.IsLinear;

                        var radius = ComputeSampleRadius(sample.Tag);
                        if (radius <= 0) continue;

                        total++;

                        var key = BuildKey(category, systemType, density, isLinear);
                        if (!buckets.TryGetValue(key, out var list))
                        {
                            list = new List<double>();
                            buckets[key] = list;
                        }
                        list.Add(radius);
                    }
                }
                catch { /* skip bad files */ }
            }

            result.SampleCount = total;
            if (buckets.Count == 0) return result;

            foreach (var kvp in buckets)
            {
                var key = kvp.Key;
                var list = kvp.Value;
                if (list.Count == 0) continue;

                list.Sort();
                var p50 = Percentile(list, 50);
                var p85 = Percentile(list, 85);
                var p95 = Percentile(list, 95);
                var mean = list.Average();

                var parts = key.Split('|');
                var entry = new TagPlacementRadiusEntry
                {
                    Key = key,
                    Category = parts.Length > 0 ? parts[0] : "",
                    SystemType = parts.Length > 1 ? parts[1] : "",
                    Density = parts.Length > 2 ? parts[2] : "",
                    IsLinear = parts.Length > 3 && bool.TryParse(parts[3], out var v) && v,
                    SampleCount = list.Count,
                    Min = list.First(),
                    Max = list.Last(),
                    Mean = mean,
                    P50 = p50,
                    P85 = p85,
                    P95 = p95,
                    Radius = p85 > 0 ? p85 : (mean > 0 ? mean : p50)
                };
                result.Radii.Add(entry);
            }

            return result;
        }

        private void SaveToFile(string path, TagPlacementRadiusFile file)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(file, options));
            }
            catch { }
        }

        private static string BuildKey(string category, string systemType, string density, bool isLinear)
        {
            return $"{category}|{systemType}|{density}|{isLinear}";
        }

        private static double ComputeSampleRadius(TrainingTag tag)
        {
            var dx = tag.OffsetX;
            var dy = tag.OffsetY;
            if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsInfinity(dx) || double.IsInfinity(dy))
                return 0;

            var radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius > 0.0001)
                return radius;

            var leader = tag.LeaderLength;
            if (leader > 0 && !double.IsNaN(leader) && !double.IsInfinity(leader))
                return leader;

            return 0;
        }

        private static double Percentile(List<double> values, int percentile)
        {
            if (values == null || values.Count == 0) return 0;
            if (percentile <= 0) return values.First();
            if (percentile >= 100) return values.Last();

            var index = (int)Math.Round((percentile / 100.0) * (values.Count - 1));
            index = Math.Max(0, Math.Min(values.Count - 1, index));
            return values[index];
        }

        private static string NormalizeSystemType(string systemType)
        {
            return (systemType ?? "").Trim();
        }

        private static string NormalizeDensity(string density)
        {
            if (string.IsNullOrWhiteSpace(density)) return "medium";
            var d = density.Trim().ToLowerInvariant();
            return d == "low" || d == "high" ? d : "medium";
        }

        private static string ContextToDensity(ElementContext context)
        {
            return context?.Density switch
            {
                DensityLevel.Low => "low",
                DensityLevel.High => "high",
                _ => "medium"
            };
        }
    }

    public class TagPlacementRadiusFile
    {
        public string Version { get; set; }
        public string UpdatedAt { get; set; }
        public int SampleCount { get; set; }
        public List<TagPlacementRadiusEntry> Radii { get; set; } = new();
    }

    public class TagPlacementRadiusEntry
    {
        public string Key { get; set; }
        public string Category { get; set; }
        public string SystemType { get; set; }
        public string Density { get; set; }
        public bool IsLinear { get; set; }
        public int SampleCount { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double P50 { get; set; }
        public double P85 { get; set; }
        public double P95 { get; set; }
        public double Radius { get; set; }
    }
}
