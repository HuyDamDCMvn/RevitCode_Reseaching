using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RevitChatLocal.Services
{
    public class OllamaConfig
    {
        public string EndpointUrl { get; set; } = "http://localhost:11434";
        public string Model { get; set; } = "qwen2.5:7b";
        public int MaxTokens { get; set; } = 4096;
        public int MaxConversationMessages { get; set; } = 40;
        public string ToolSelectionMode { get; set; } = "smart";
        public List<string> EnabledSkillPacks { get; set; } = new()
        {
            "Core", "ViewControl", "MEP", "Modeler", "BIMCoordinator", "LinkedModels"
        };
    }

    public static class LocalConfigService
    {
        private static readonly object _lock = new();
        private static readonly string ConfigDir;
        private static readonly string ConfigPath;
        private static OllamaConfig _cached;

        static LocalConfigService()
        {
            var loc = typeof(LocalConfigService).Assembly.Location;
            var dllDir = !string.IsNullOrEmpty(loc)
                ? Path.GetDirectoryName(loc)
                : AppContext.BaseDirectory;
            ConfigDir = Path.Combine(dllDir, "Data", "Config");
            ConfigPath = Path.Combine(ConfigDir, "ollama_config.json");
        }

        public static OllamaConfig Load()
        {
            lock (_lock)
            {
                if (_cached != null) return _cached;

                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        var json = File.ReadAllText(ConfigPath);
                        _cached = JsonSerializer.Deserialize<OllamaConfig>(json) ?? new OllamaConfig();
                        return _cached;
                    }
                }
                catch
                {
                }

                _cached = new OllamaConfig();
                return _cached;
            }
        }

        public static void Save(OllamaConfig config)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(ConfigPath, json);
                    _cached = config;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to save config: {ex.Message}", ex);
                }
            }
        }

        public static bool HasEndpoint()
        {
            var config = Load();
            return !string.IsNullOrWhiteSpace(config.EndpointUrl);
        }

        public static void InvalidateCache()
        {
            lock (_lock) { _cached = null; }
        }
    }
}
