using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmartTag.ML;

namespace SmartTag.Services
{
    /// <summary>
    /// After exporting training data from Revit, ingests the JSON and updates learned overrides
    /// so the next tag placement uses the exported patterns (self-learning).
    /// </summary>
    public class ExportIngestionService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Load exported JSON, aggregate by category (and category+system), update learned_overrides.json.
        /// Call this right after a successful export.
        /// </summary>
        public IngestResult IngestAndUpdate(string exportedJsonPath)
        {
            var result = new IngestResult();

            if (string.IsNullOrEmpty(exportedJsonPath) || !File.Exists(exportedJsonPath))
            {
                result.Success = false;
                result.Message = "File not found: " + (exportedJsonPath ?? "");
                return result;
            }

            try
            {
                var json = File.ReadAllText(exportedJsonPath);
                var data = JsonSerializer.Deserialize<TrainingDataFile>(json, JsonOptions);
                if (data?.Samples == null || data.Samples.Count == 0)
                {
                    result.Success = false;
                    result.Message = "No samples in file.";
                    return result;
                }

                var byCategory = AggregateByCategory(data.Samples);
                var byCategoryAndSystem = AggregateByCategoryAndSystem(data.Samples);

                var newData = new LearnedOverridesData
                {
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow.ToString("o"),
                    ByCategory = byCategory,
                    ByCategoryAndSystem = byCategoryAndSystem
                };

                LearnedOverridesService.Instance.UpdateOverrides(newData);

                result.Success = true;
                result.SamplesProcessed = data.Samples.Count;
                result.CategoriesUpdated = byCategory.Count;
                result.CategorySystemsUpdated = byCategoryAndSystem.Count;
                result.Message = $"Learned from {result.SamplesProcessed} samples: {result.CategoriesUpdated} categories, {result.CategorySystemsUpdated} category+system overrides.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Ingest failed: " + ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Ingest all JSON files in the annotated folder and merge into learned overrides.
        /// </summary>
        public IngestResult IngestAllInFolder(string annotatedFolderPath)
        {
            var result = new IngestResult();

            if (string.IsNullOrEmpty(annotatedFolderPath) || !Directory.Exists(annotatedFolderPath))
            {
                result.Success = false;
                result.Message = "Folder not found.";
                return result;
            }

            var files = Directory.GetFiles(annotatedFolderPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                .ToList();

            if (files.Count == 0)
            {
                result.Success = false;
                result.Message = "No JSON files in folder.";
                return result;
            }

            var allByCategory = new Dictionary<string, LearnedOverride>();
            var allByCategoryAndSystem = new Dictionary<string, LearnedOverride>();

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<TrainingDataFile>(json, JsonOptions);
                    if (data?.Samples == null) continue;

                    var byCat = AggregateByCategory(data.Samples);
                    var byCatSys = AggregateByCategoryAndSystem(data.Samples);

                    MergeOverrides(allByCategory, byCat);
                    MergeOverrides(allByCategoryAndSystem, byCatSys);
                    result.SamplesProcessed += data.Samples.Count;
                }
                catch { /* skip bad files */ }
            }

            var newData = new LearnedOverridesData
            {
                Version = 1,
                UpdatedAt = DateTime.UtcNow.ToString("o"),
                ByCategory = allByCategory,
                ByCategoryAndSystem = allByCategoryAndSystem
            };

            LearnedOverridesService.Instance.UpdateOverrides(newData);
            result.Success = true;
            result.CategoriesUpdated = allByCategory.Count;
            result.CategorySystemsUpdated = allByCategoryAndSystem.Count;
            result.Message = $"Learned from {result.SamplesProcessed} samples across {files.Count} files.";
            return result;
        }

        private static Dictionary<string, LearnedOverride> AggregateByCategory(List<TrainingSample> samples)
        {
            if (samples == null) return new Dictionary<string, LearnedOverride>(StringComparer.OrdinalIgnoreCase);
            var groups = samples
                .Where(s => s != null && !string.IsNullOrEmpty(s.Element?.Category))
                .GroupBy(s => s.Element.Category.Trim(), StringComparer.OrdinalIgnoreCase);

            var dict = new Dictionary<string, LearnedOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var ov = AggregateGroup(g.ToList());
                if (ov.SampleCount > 0)
                    dict[g.Key] = ov;
            }
            return dict;
        }

        private static Dictionary<string, LearnedOverride> AggregateByCategoryAndSystem(List<TrainingSample> samples)
        {
            if (samples == null) return new Dictionary<string, LearnedOverride>(StringComparer.OrdinalIgnoreCase);
            var groups = samples
                .Where(s => s != null && !string.IsNullOrEmpty(s.Element?.Category))
                .GroupBy(s => $"{s.Element.Category.Trim()}|{(s.Element.SystemType ?? "").Trim()}", StringComparer.OrdinalIgnoreCase);

            var dict = new Dictionary<string, LearnedOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var sys = g.First().Element?.SystemType;
                if (string.IsNullOrWhiteSpace(sys)) continue; // skip generic category-only
                var ov = AggregateGroup(g.ToList());
                if (ov.SampleCount > 0)
                    dict[g.Key] = ov;
            }
            return dict;
        }

        private static LearnedOverride AggregateGroup(List<TrainingSample> group)
        {
            if (group == null) return new LearnedOverride();
            var valid = group.Where(s => s != null).ToList();
            var positions = valid
                .Where(s => !string.IsNullOrEmpty(s.Tag?.Position))
                .Select(s => s.Tag.Position.Trim())
                .ToList();

            var positionCounts = positions
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .Select(x => x.Key)
                .ToList();

            var hasLeaderCount = valid.Count(s => s.Tag?.HasLeader == true);
            var addLeader = valid.Count > 0 && hasLeaderCount > (valid.Count / 2);

            var alignRowCount = valid.Count(s => s.Tag != null && s.Tag.AlignedWithRow);
            var alignColCount = valid.Count(s => s.Tag != null && s.Tag.AlignedWithColumn);
            var preferAlignRow = valid.Count > 0 && alignRowCount > (valid.Count / 2);
            var preferAlignColumn = valid.Count > 0 && alignColCount > (valid.Count / 2);

            double? avgOffset = null;
            var withOffset = valid.Where(s => s.Tag != null).ToList();
            if (withOffset.Count > 0)
            {
                var avgX = withOffset.Average(s => Math.Abs(s.Tag.OffsetX));
                var avgY = withOffset.Average(s => Math.Abs(s.Tag.OffsetY));
                avgOffset = (avgX + avgY) / 2.0;
                if (avgOffset < 0.01) avgOffset = null;
            }

            return new LearnedOverride
            {
                PreferredPositions = positionCounts.Count > 0 ? positionCounts : new List<string> { "TopRight" },
                AddLeader = addLeader,
                OffsetDistance = avgOffset,
                PreferAlignRow = preferAlignRow,
                PreferAlignColumn = preferAlignColumn,
                SampleCount = valid.Count
            };
        }

        private static void MergeOverrides(
            Dictionary<string, LearnedOverride> target,
            Dictionary<string, LearnedOverride> source)
        {
            foreach (var kv in source)
            {
                if (target.TryGetValue(kv.Key, out var existing))
                {
                    // Merge: prefer larger sample count, or average
                    if (kv.Value.SampleCount >= existing.SampleCount)
                        target[kv.Key] = kv.Value;
                }
                else
                {
                    target[kv.Key] = kv.Value;
                }
            }
        }

        public class IngestResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public int SamplesProcessed { get; set; }
            public int CategoriesUpdated { get; set; }
            public int CategorySystemsUpdated { get; set; }
        }
    }
}
