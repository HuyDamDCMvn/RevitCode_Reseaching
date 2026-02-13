using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmartTag.ML;

namespace SmartTag.Services
{
    /// <summary>
    /// Auto-tune scoring weights based on annotated training data statistics.
    /// </summary>
    public class AutoTuneWeightsService
    {
        private static AutoTuneWeightsService _instance;
        private static readonly object _lock = new();
        private AutoTunedWeights _cached;
        private string _weightsPath;

        public static AutoTuneWeightsService Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= new AutoTuneWeightsService(); }
                return _instance;
            }
        }

        private AutoTuneWeightsService() { }

        public AutoTunedWeights GetWeights()
        {
            if (_cached != null) return _cached;

            lock (_lock)
            {
                if (_cached != null) return _cached;

                var path = GetWeightsPath();
                if (File.Exists(path))
                {
                    try
                    {
                        var json = File.ReadAllText(path);
                        var file = JsonSerializer.Deserialize<AutoTunedWeightsFile>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (file?.Weights != null)
                        {
                            _cached = file.Weights;
                            return _cached;
                        }
                    }
                    catch { }
                }

                var annotatedPath = GetAnnotatedFolderPath();
                var built = BuildFromAnnotatedFolder(annotatedPath);
                _cached = built.Weights;
                SaveToFile(path, built);
                return _cached;
            }
        }

        private string GetWeightsPath()
        {
            if (!string.IsNullOrEmpty(_weightsPath))
                return _weightsPath;

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var assemblyDir = Path.GetDirectoryName(assembly.Location);
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "Data", "Training", "auto_weights.json"),
                Path.Combine(assemblyDir, "..", "Data", "Training", "auto_weights.json"),
                Path.Combine(assemblyDir, "..", "..", "src", "SmartTag", "Data", "Training", "auto_weights.json"),
                Path.Combine(Environment.CurrentDirectory, "Data", "Training", "auto_weights.json"),
                @"D:\03_DCMvn\RevitCode\src\SmartTag\Data\Training\auto_weights.json"
            };

            foreach (var path in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(path);
                    var dir = Path.GetDirectoryName(full);
                    if (Directory.Exists(dir) || dir != null)
                    {
                        _weightsPath = full;
                        return full;
                    }
                }
                catch { }
            }

            _weightsPath = Path.Combine(Path.GetTempPath(), "SmartTag_auto_weights.json");
            return _weightsPath;
        }

        private string GetAnnotatedFolderPath()
        {
            var weightsPath = GetWeightsPath();
            var trainingDir = Path.GetDirectoryName(weightsPath);
            return string.IsNullOrEmpty(trainingDir) ? null : Path.Combine(trainingDir, "annotated");
        }

        private AutoTunedWeightsFile BuildFromAnnotatedFolder(string annotatedFolderPath)
        {
            var result = new AutoTunedWeightsFile
            {
                Version = "1.0",
                UpdatedAt = DateTime.UtcNow.ToString("o"),
                Weights = new AutoTunedWeights()
            };

            if (string.IsNullOrEmpty(annotatedFolderPath) || !Directory.Exists(annotatedFolderPath))
                return result;

            var files = Directory.GetFiles(annotatedFolderPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0) return result;

            int total = 0;
            int alignRowCount = 0;
            int alignColCount = 0;
            int leaderCount = 0;
            int densityHigh = 0;

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
                        if (sample?.Tag == null) continue;
                        total++;
                        if (sample.Tag.AlignedWithRow) alignRowCount++;
                        if (sample.Tag.AlignedWithColumn) alignColCount++;
                        if (sample.Tag.HasLeader) leaderCount++;
                        if (sample.Context?.Density != null &&
                            sample.Context.Density.Equals("high", StringComparison.OrdinalIgnoreCase))
                            densityHigh++;
                    }
                }
                catch { /* skip bad files */ }
            }

            if (total <= 0) return result;

            var rowRate = (double)alignRowCount / total;
            var colRate = (double)alignColCount / total;
            var alignRate = (rowRate + colRate) / 2.0;
            var leaderRate = (double)leaderCount / total;
            var densityHighRate = (double)densityHigh / total;

            result.SampleCount = total;
            result.AlignmentRate = alignRate;
            result.ColumnAlignmentRate = colRate;
            result.LeaderRate = leaderRate;
            result.HighDensityRate = densityHighRate;

            result.Weights.AlignmentBonusMultiplier = Clamp(0.8 + 0.8 * alignRate, 0.6, 1.8);
            result.Weights.ColumnAlignBonusMultiplier = Clamp(0.8 + 0.8 * colRate, 0.6, 1.8);
            result.Weights.PreferenceBonusMultiplier = Clamp(0.9 + 0.4 * (1 - alignRate), 0.7, 1.4);
            result.Weights.CollisionPenaltyMultiplier = Clamp(1.0 + 0.5 * densityHighRate, 0.8, 1.8);
            result.Weights.LeaderLengthPenaltyMultiplier = leaderRate > 0.6 ? 0.6 : 1.0;
            result.Weights.NearEdgeBonusMultiplier = 1.0;

            return result;
        }

        private void SaveToFile(string path, AutoTunedWeightsFile file)
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

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    public class AutoTunedWeightsFile
    {
        public string Version { get; set; }
        public string UpdatedAt { get; set; }
        public int SampleCount { get; set; }
        public double AlignmentRate { get; set; }
        public double ColumnAlignmentRate { get; set; }
        public double LeaderRate { get; set; }
        public double HighDensityRate { get; set; }
        public AutoTunedWeights Weights { get; set; } = new();
    }

    public class AutoTunedWeights
    {
        public double AlignmentBonusMultiplier { get; set; } = 1.0;
        public double ColumnAlignBonusMultiplier { get; set; } = 1.0;
        public double PreferenceBonusMultiplier { get; set; } = 1.0;
        public double CollisionPenaltyMultiplier { get; set; } = 1.0;
        public double LeaderLengthPenaltyMultiplier { get; set; } = 1.0;
        public double NearEdgeBonusMultiplier { get; set; } = 1.0;
    }
}
