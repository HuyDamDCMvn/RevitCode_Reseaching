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
    /// Runtime RL policy library (trained via tools/RLTraining/train_dqn.py).
    /// Provides action scores to bias placement.
    /// </summary>
    public class RLPolicyLibrary
    {
        private static RLPolicyLibrary _instance;
        private static readonly object _lock = new();

        private readonly Dictionary<string, RLPolicy> _policiesByKey =
            new(StringComparer.OrdinalIgnoreCase);

        private string _policyPath;

        public static RLPolicyLibrary Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= new RLPolicyLibrary(); }
                return _instance;
            }
        }

        private RLPolicyLibrary() { }

        public void EnsureLoaded()
        {
            if (_policiesByKey.Count > 0) return;

            lock (_lock)
            {
                if (_policiesByKey.Count > 0) return;

                var path = GetPolicyPath();
                if (!File.Exists(path))
                    return;

                try
                {
                    var json = File.ReadAllText(path);
                    var file = JsonSerializer.Deserialize<RLPolicyFile>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (file?.Policies == null) return;

                    _policiesByKey.Clear();
                    foreach (var p in file.Policies)
                    {
                        if (string.IsNullOrEmpty(p.Key)) continue;
                        _policiesByKey[p.Key] = p;
                    }
                }
                catch { }
            }
        }

        public RLPolicy GetBestPolicy(TaggableElement element, ElementContext context)
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
            if (_policiesByKey.TryGetValue(key, out var exact))
                return exact;

            var candidates = _policiesByKey.Values
                .Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
                return null;

            int Score(RLPolicy p)
            {
                int score = 0;
                if (p.IsLinear == isLinear) score += 2;
                if (!string.IsNullOrEmpty(systemType) &&
                    p.SystemType.Equals(systemType, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (p.Density.Equals(density, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (p.ParallelBucket.Equals(parallelBucket, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (p.OrientationBucket.Equals(orientationBucket, StringComparison.OrdinalIgnoreCase)) score += 2;
                return score;
            }

            return candidates
                .OrderByDescending(Score)
                .ThenByDescending(p => p.SampleCount)
                .FirstOrDefault();
        }

        private string GetPolicyPath()
        {
            if (!string.IsNullOrEmpty(_policyPath))
                return _policyPath;

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "Data", "Training", "rl_policy.json"),
                Path.Combine(assemblyDir, "..", "Data", "Training", "rl_policy.json"),
                Path.Combine(assemblyDir, "..", "..", "src", "SmartTag", "Data", "Training", "rl_policy.json"),
                Path.Combine(Environment.CurrentDirectory, "Data", "Training", "rl_policy.json"),
                @"D:\03_DCMvn\RevitCode\src\SmartTag\Data\Training\rl_policy.json"
            };

            foreach (var path in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(full);
                    if (Directory.Exists(dir) || dir != null)
                    {
                        _policyPath = full;
                        return full;
                    }
                }
                catch { }
            }

            _policyPath = Path.Combine(Path.GetTempPath(), "SmartTag_rl_policy.json");
            return _policyPath;
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
            var normalized = Math.Abs(degrees % 180.0);
            if (normalized <= 15 || normalized >= 165)
                return "H";
            if (Math.Abs(normalized - 90) <= 15)
                return "V";
            return "D";
        }
    }

    public class RLPolicyFile
    {
        public string Version { get; set; }
        public string UpdatedAt { get; set; }
        public List<RLPolicy> Policies { get; set; } = new();
    }

    public class RLPolicy
    {
        public string Key { get; set; }
        public string Category { get; set; }
        public string SystemType { get; set; }
        public string Density { get; set; }
        public string ParallelBucket { get; set; }
        public string OrientationBucket { get; set; }
        public bool IsLinear { get; set; }
        public int SampleCount { get; set; }
        public Dictionary<string, double> ActionScores { get; set; } = new();

        public bool PreferAlignRow =>
            ActionScores.TryGetValue("AlignRow", out var s) && s > 0;

        public bool PreferAlignColumn =>
            ActionScores.TryGetValue("AlignColumn", out var s) && s > 0;

        public double? GetNormalizedPositionScore(TagPosition position)
        {
            if (ActionScores == null || ActionScores.Count == 0)
                return null;

            var posKey = position.ToString();
            if (!ActionScores.TryGetValue(posKey, out var score))
                return null;

            var positionScores = ActionScores
                .Where(kv => IsPositionKey(kv.Key))
                .Select(kv => kv.Value)
                .ToList();

            if (positionScores.Count == 0)
                return null;

            var min = positionScores.Min();
            var max = positionScores.Max();
            if (Math.Abs(max - min) < 1e-6)
                return 0.5;

            return (score - min) / (max - min);
        }

        private static bool IsPositionKey(string key)
        {
            return key == "TopRight" || key == "TopLeft" || key == "TopCenter" ||
                   key == "BottomRight" || key == "BottomLeft" || key == "BottomCenter" ||
                   key == "Right" || key == "Left" || key == "Center";
        }
    }
}
