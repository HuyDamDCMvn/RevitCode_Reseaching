using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevitChat.Services
{
    public class AnalyticsService
    {
        private readonly string _baseDir;

        public AnalyticsService(string baseDir)
        {
            _baseDir = baseDir;
        }

        public void SaveModelHealthScore(string projectName, string documentGuid, int score, Dictionary<string, int> breakdown)
        {
            var entry = new ModelHealthEntry
            {
                ProjectName = projectName,
                DocumentGuid = documentGuid,
                Score = score,
                Breakdown = breakdown,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                var dir = Path.Combine(_baseDir, "Analytics");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{documentGuid}_health.jsonl");
                var json = JsonSerializer.Serialize(entry);
                File.AppendAllText(path, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnalyticsService] SaveModelHealthScore: {ex.Message}");
            }
        }

        public List<ModelHealthEntry> GetHealthHistory(string documentGuid, int limit = 50)
        {
            try
            {
                var path = Path.Combine(_baseDir, "Analytics", $"{documentGuid}_health.jsonl");
                if (!File.Exists(path)) return new List<ModelHealthEntry>();
                return File.ReadAllLines(path)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => JsonSerializer.Deserialize<ModelHealthEntry>(l))
                    .Where(e => e != null)
                    .OrderByDescending(e => e.Timestamp)
                    .Take(limit)
                    .ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnalyticsService] GetHealthHistory: {ex.Message}");
                return new List<ModelHealthEntry>();
            }
        }

        public Dictionary<string, int> CompareProjects()
        {
            var result = new Dictionary<string, int>();
            try
            {
                var dir = Path.Combine(_baseDir, "Analytics");
                if (!Directory.Exists(dir)) return result;
                foreach (var file in Directory.GetFiles(dir, "*_health.jsonl"))
                {
                    var lines = File.ReadAllLines(file);
                    if (lines.Length == 0) continue;
                    var last = JsonSerializer.Deserialize<ModelHealthEntry>(lines.Last());
                    if (last != null)
                        result[last.ProjectName ?? Path.GetFileNameWithoutExtension(file)] = last.Score;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AnalyticsService] CompareProjects: {ex.Message}");
            }
            return result;
        }

        public class ModelHealthEntry
        {
            public string ProjectName { get; set; }
            public string DocumentGuid { get; set; }
            public int Score { get; set; }
            public Dictionary<string, int> Breakdown { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
