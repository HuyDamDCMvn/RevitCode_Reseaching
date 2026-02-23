using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevitChat.Services
{
    public class ProjectTrainingService
    {
        private readonly string _baseDir;
        private System.Collections.Concurrent.ConcurrentDictionary<string, List<ProjectApprovedExample>> _projectExamples = new();

        public ProjectTrainingService(string baseDir)
        {
            _baseDir = baseDir;
        }

        public void SaveProjectExample(string projectGuid, string prompt, string toolName, Dictionary<string, object> args)
        {
            var list = _projectExamples.GetOrAdd(projectGuid, _ => new List<ProjectApprovedExample>());
            lock (list)
            {
                list.Add(new ProjectApprovedExample
            {
                Prompt = prompt,
                ToolName = toolName,
                Args = args,
                Timestamp = DateTime.UtcNow
            });
            }

            SaveToFile(projectGuid);
        }

        public List<ProjectApprovedExample> GetProjectExamples(string projectGuid, int limit = 10)
        {
            var list = _projectExamples.GetOrAdd(projectGuid, _ => LoadFromFile(projectGuid));
            lock (list)
            {
                return list.OrderByDescending(e => e.Timestamp).Take(limit).ToList();
            }
        }

        private void SaveToFile(string projectGuid)
        {
            try
            {
                if (!_projectExamples.TryGetValue(projectGuid, out var list)) return;
                List<ProjectApprovedExample> snapshot;
                lock (list)
                {
                    snapshot = list.ToList();
                }
                var dir = Path.Combine(_baseDir, "ProjectTraining");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{projectGuid}.json");
                var json = JsonSerializer.Serialize(snapshot,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectTrainingService] SaveToFile: {ex.Message}");
            }
        }

        private List<ProjectApprovedExample> LoadFromFile(string projectGuid)
        {
            try
            {
                var path = Path.Combine(_baseDir, "ProjectTraining", $"{projectGuid}.json");
                if (!File.Exists(path)) return new List<ProjectApprovedExample>();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<ProjectApprovedExample>>(json) ?? new List<ProjectApprovedExample>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectTrainingService] LoadFromFile: {ex.Message}");
                return new List<ProjectApprovedExample>();
            }
        }

        public class ProjectApprovedExample
        {
            public string Prompt { get; set; }
            public string ToolName { get; set; }
            public Dictionary<string, object> Args { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}
