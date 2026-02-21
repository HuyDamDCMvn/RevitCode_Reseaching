using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RevitChatLocal.Services
{
    public class OllamaConfig
    {
        public string EndpointUrl { get; set; } = "http://localhost:11434";
        public string Model { get; set; } = "qwen2.5:7b";
        public int MaxTokens { get; set; } = 4096;
        public int MaxConversationMessages { get; set; } = 40;
    }

    public static class LocalConfigService
    {
        private static readonly string DllDir =
            Path.GetDirectoryName(typeof(LocalConfigService).Assembly.Location);
        private static readonly string ConfigDir = Path.Combine(DllDir, "Data", "Config");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "ollama_config.json");
        private static OllamaConfig _cached;

        public static OllamaConfig Load()
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

        public static void Save(OllamaConfig config)
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

        public static bool HasEndpoint()
        {
            var config = Load();
            return !string.IsNullOrWhiteSpace(config.EndpointUrl);
        }

        public static void InvalidateCache() => _cached = null;
    }
}
