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
    /// Template-based placement library built from annotated training data.
    /// Provides context-aware preferred positions and offsets.
    /// </summary>
    public class TemplateLibrary
    {
        private static TemplateLibrary _instance;
        private static readonly object _lock = new();

        private readonly Dictionary<string, PlacementTemplate> _templatesByKey =
            new(StringComparer.OrdinalIgnoreCase);

        private string _templatesPath;

        public static TemplateLibrary Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= new TemplateLibrary(); }
                return _instance;
            }
        }

        private TemplateLibrary() { }

        public void ForceReload()
        {
            lock (_lock)
            {
                _templatesByKey.Clear();
                var annotatedPath = GetAnnotatedFolderPath();
                BuildFromAnnotatedFolder(annotatedPath);
                SaveToFile(GetTemplatesPath());
            }
        }

        public void EnsureLoaded()
        {
            if (_templatesByKey.Count > 0) return;

            lock (_lock)
            {
                if (_templatesByKey.Count > 0) return;

                var annotatedPath = GetAnnotatedFolderPath();
                var templatesPath = GetTemplatesPath();
                var shouldRebuild = !File.Exists(templatesPath) || IsAnnotatedNewer(annotatedPath, templatesPath);

                if (!shouldRebuild && TryLoadFromFile(templatesPath))
                    return;

                BuildFromAnnotatedFolder(annotatedPath);
                SaveToFile(templatesPath);
            }
        }

        public PlacementTemplate GetBestTemplate(TaggableElement element, ElementContext context)
        {
            if (element == null) return null;

            EnsureLoaded();

            var category = (element.BuiltInCategoryName ?? element.CategoryName ?? "").Trim();
            var systemType = NormalizeSystemType(element.SystemClassification ?? element.SystemName ?? "");
            var density = ContextToDensity(context);
            var parallelBucket = GetParallelBucket(context?.ParallelElementsCount ?? 0);
            var orientationBucket = GetOrientationBucket(element.AngleRadians);
            var isLinear = element.IsLinearElement;

            var key = BuildKey(category, systemType, density, parallelBucket, orientationBucket, isLinear);
            if (_templatesByKey.TryGetValue(key, out var exact))
                return exact;

            // Fallback search: score candidates by similarity
            var candidates = _templatesByKey.Values
                .Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                return null;

            int Score(PlacementTemplate t)
            {
                int score = 0;
                if (t.IsLinear == isLinear) score += 2;
                if (!string.IsNullOrEmpty(systemType) &&
                    t.SystemType.Equals(systemType, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (t.Density.Equals(density, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (t.ParallelBucket.Equals(parallelBucket, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (t.OrientationBucket.Equals(orientationBucket, StringComparison.OrdinalIgnoreCase)) score += 2;
                return score;
            }

            return candidates
                .OrderByDescending(Score)
                .ThenByDescending(t => t.SampleCount)
                .FirstOrDefault();
        }

        private string GetTemplatesPath()
        {
            if (!string.IsNullOrEmpty(_templatesPath))
                return _templatesPath;

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "Data", "Training", "templates.json"),
                Path.Combine(assemblyDir, "..", "Data", "Training", "templates.json"),
                Path.Combine(assemblyDir, "..", "..", "src", "SmartTag", "Data", "Training", "templates.json"),
                Path.Combine(Environment.CurrentDirectory, "Data", "Training", "templates.json"),
                @"D:\03_DCMvn\RevitCode\src\SmartTag\Data\Training\templates.json"
            };

            foreach (var path in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(full);
                    if (Directory.Exists(dir) || dir != null)
                    {
                        _templatesPath = full;
                        return full;
                    }
                }
                catch { }
            }

            _templatesPath = Path.Combine(Path.GetTempPath(), "SmartTag_templates.json");
            return _templatesPath;
        }

        private string GetAnnotatedFolderPath()
        {
            var templatesPath = GetTemplatesPath();
            var trainingDir = Path.GetDirectoryName(templatesPath);
            return string.IsNullOrEmpty(trainingDir) ? null : Path.Combine(trainingDir, "annotated");
        }

        private bool IsAnnotatedNewer(string annotatedFolderPath, string templatesPath)
        {
            try
            {
                if (string.IsNullOrEmpty(annotatedFolderPath) || !Directory.Exists(annotatedFolderPath))
                    return false;

                var templateTime = File.Exists(templatesPath)
                    ? File.GetLastWriteTimeUtc(templatesPath)
                    : DateTime.MinValue;

                var latestAnnotated = Directory.GetFiles(annotatedFolderPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();

                return latestAnnotated > templateTime;
            }
            catch
            {
                return false;
            }
        }

        private bool TryLoadFromFile(string templatesPath)
        {
            try
            {
                if (!File.Exists(templatesPath)) return false;
                var json = File.ReadAllText(templatesPath);
                var file = JsonSerializer.Deserialize<PlacementTemplateFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (file?.Templates == null || file.Templates.Count == 0)
                    return false;

                _templatesByKey.Clear();
                foreach (var t in file.Templates)
                {
                    if (string.IsNullOrEmpty(t.Key)) continue;
                    _templatesByKey[t.Key] = t;
                }
                return _templatesByKey.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private void SaveToFile(string templatesPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(templatesPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var file = new PlacementTemplateFile
                {
                    Version = "1.0",
                    UpdatedAt = DateTime.UtcNow.ToString("o"),
                    Templates = _templatesByKey.Values.OrderByDescending(t => t.SampleCount).ToList()
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(templatesPath, JsonSerializer.Serialize(file, options));
            }
            catch { }
        }

        private void BuildFromAnnotatedFolder(string annotatedFolderPath)
        {
            _templatesByKey.Clear();
            if (string.IsNullOrEmpty(annotatedFolderPath) || !Directory.Exists(annotatedFolderPath))
                return;

            var files = Directory.GetFiles(annotatedFolderPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0) return;

            var aggregates = new Dictionary<string, TemplateAggregate>(StringComparer.OrdinalIgnoreCase);

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
                        var parallelBucket = GetParallelBucket(sample.Context?.ParallelElementsCount ?? 0);
                        var orientationBucket = GetOrientationBucketDegrees(sample.Element.Orientation);
                        var isLinear = sample.Element.IsLinear;

                        var key = BuildKey(category, systemType, density, parallelBucket, orientationBucket, isLinear);
                        if (!aggregates.TryGetValue(key, out var agg))
                        {
                            agg = new TemplateAggregate
                            {
                                Category = category,
                                SystemType = systemType,
                                Density = density,
                                ParallelBucket = parallelBucket,
                                OrientationBucket = orientationBucket,
                                IsLinear = isLinear
                            };
                            aggregates[key] = agg;
                        }

                        agg.AddSample(sample);
                    }
                }
                catch { /* skip bad files */ }
            }

            foreach (var kv in aggregates)
            {
                var template = kv.Value.ToTemplate();
                template.Key = kv.Key;
                _templatesByKey[kv.Key] = template;
            }
        }

        private static string BuildKey(
            string category,
            string systemType,
            string density,
            string parallelBucket,
            string orientationBucket,
            bool isLinear)
        {
            return $"{category}|{systemType}|{density}|{parallelBucket}|{orientationBucket}|{isLinear}";
        }

        private static string NormalizeSystemType(string systemType)
        {
            return (systemType ?? "").Trim();
        }

        private static string NormalizeDensity(string density)
        {
            if (string.IsNullOrEmpty(density)) return "medium";
            return density.Trim().ToLowerInvariant() switch
            {
                "low" => "low",
                "high" => "high",
                _ => "medium"
            };
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

        private static string GetParallelBucket(int count)
        {
            if (count <= 0) return "0";
            if (count <= 2) return "1-2";
            return "3+";
        }

        private static string GetOrientationBucket(double angleRadians)
        {
            var degrees = angleRadians * 180.0 / Math.PI;
            return GetOrientationBucketDegrees(degrees);
        }

        private static string GetOrientationBucketDegrees(double degrees)
        {
            var normalized = Math.Abs(degrees % 180.0);
            if (normalized <= 15 || normalized >= 165)
                return "H";
            if (Math.Abs(normalized - 90) <= 15)
                return "V";
            return "D";
        }

        private class TemplateAggregate
        {
            public string Category { get; set; }
            public string SystemType { get; set; }
            public string Density { get; set; }
            public string ParallelBucket { get; set; }
            public string OrientationBucket { get; set; }
            public bool IsLinear { get; set; }

            public int SampleCount { get; private set; }
            public double OffsetXSum { get; private set; }
            public double OffsetYSum { get; private set; }
            public int LeaderCount { get; private set; }
            public double LeaderLengthSum { get; private set; }
            public int AlignRowCount { get; private set; }
            public int AlignColCount { get; private set; }
            public readonly Dictionary<string, int> PositionCounts = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> RotationCounts = new(StringComparer.OrdinalIgnoreCase);

            public void AddSample(TrainingSample sample)
            {
                SampleCount++;
                OffsetXSum += sample.Tag.OffsetX;
                OffsetYSum += sample.Tag.OffsetY;
                if (sample.Tag.HasLeader)
                {
                    LeaderCount++;
                    LeaderLengthSum += sample.Tag.LeaderLength;
                }
                if (sample.Tag.AlignedWithRow) AlignRowCount++;
                if (sample.Tag.AlignedWithColumn) AlignColCount++;

                var pos = sample.Tag.Position ?? "";
                if (!PositionCounts.ContainsKey(pos)) PositionCounts[pos] = 0;
                PositionCounts[pos]++;

                var rot = sample.Tag.Rotation ?? "Horizontal";
                if (!RotationCounts.ContainsKey(rot)) RotationCounts[rot] = 0;
                RotationCounts[rot]++;
            }

            public PlacementTemplate ToTemplate()
            {
                var preferredPositions = PositionCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .ToList();

                var rotation = RotationCounts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault() ?? "Horizontal";

                var leaderRatio = SampleCount > 0 ? (double)LeaderCount / SampleCount : 0;
                var avgLeaderLength = LeaderCount > 0 ? LeaderLengthSum / LeaderCount : 0;

                return new PlacementTemplate
                {
                    Category = Category,
                    SystemType = SystemType,
                    Density = Density,
                    ParallelBucket = ParallelBucket,
                    OrientationBucket = OrientationBucket,
                    IsLinear = IsLinear,
                    SampleCount = SampleCount,
                    PreferredPositions = preferredPositions,
                    AvgOffsetX = SampleCount > 0 ? OffsetXSum / SampleCount : 0,
                    AvgOffsetY = SampleCount > 0 ? OffsetYSum / SampleCount : 0,
                    HasLeader = leaderRatio >= 0.5,
                    LeaderLength = avgLeaderLength,
                    PreferAlignRow = AlignRowCount > SampleCount / 2,
                    PreferAlignColumn = AlignColCount > SampleCount / 2,
                    Rotation = rotation
                };
            }
        }
    }

    public class PlacementTemplateFile
    {
        public string Version { get; set; }
        public string UpdatedAt { get; set; }
        public List<PlacementTemplate> Templates { get; set; } = new();
    }

    public class PlacementTemplate
    {
        public string Key { get; set; }
        public string Category { get; set; }
        public string SystemType { get; set; }
        public string Density { get; set; }
        public string ParallelBucket { get; set; }
        public string OrientationBucket { get; set; }
        public bool IsLinear { get; set; }
        public int SampleCount { get; set; }

        public List<string> PreferredPositions { get; set; } = new();
        public double AvgOffsetX { get; set; }
        public double AvgOffsetY { get; set; }
        public bool HasLeader { get; set; }
        public double LeaderLength { get; set; }
        public bool PreferAlignRow { get; set; }
        public bool PreferAlignColumn { get; set; }
        public string Rotation { get; set; }
    }
}
