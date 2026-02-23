using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevitChat.Services
{
    public class SmartTagSyncService
    {
        private readonly string _sharedStatePath;

        public SmartTagSyncService(string baseDir)
        {
            _sharedStatePath = Path.Combine(baseDir, "smarttag_state.json");
        }

        public SmartTagState ReadState()
        {
            try
            {
                if (!File.Exists(_sharedStatePath)) return new SmartTagState();
                var json = File.ReadAllText(_sharedStatePath);
                return JsonSerializer.Deserialize<SmartTagState>(json) ?? new SmartTagState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SmartTagSyncService] ReadState: {ex.Message}");
                return new SmartTagState();
            }
        }

        public void WriteState(SmartTagState state)
        {
            try
            {
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_sharedStatePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SmartTagSyncService] WriteState: {ex.Message}");
            }
        }

        public string GetSyncSummary()
        {
            var state = ReadState();
            return JsonSerializer.Serialize(new
            {
                last_run = state.LastRunTimestamp,
                tagged_count = state.TaggedElementIds?.Count ?? 0,
                categories_processed = state.CategoriesProcessed,
                view_name = state.ViewName
            });
        }

        public class SmartTagState
        {
            public DateTime? LastRunTimestamp { get; set; }
            public List<long> TaggedElementIds { get; set; } = new();
            public List<string> CategoriesProcessed { get; set; } = new();
            public string ViewName { get; set; }
            public int TotalPlaced { get; set; }
            public int CollisionsResolved { get; set; }
        }
    }
}
