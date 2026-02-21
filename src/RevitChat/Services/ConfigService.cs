using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RevitChat.Services
{
    public class ChatConfig
    {
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "gpt-4o-mini";
        public int MaxTokens { get; set; } = 4096;
        public int MaxConversationMessages { get; set; } = 40;
    }

    public static class ConfigService
    {
        private static readonly string DllDir =
            Path.GetDirectoryName(typeof(ConfigService).Assembly.Location);
        private static readonly string ConfigDir = Path.Combine(DllDir, "Data", "Config");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "openai_config.json");
        private static ChatConfig _cached;

        public static ChatConfig Load()
        {
            if (_cached != null) return _cached;

            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    _cached = JsonSerializer.Deserialize<ChatConfig>(json) ?? new ChatConfig();
                    return _cached;
                }
            }
            catch
            {
                // Fall through to default
            }

            _cached = new ChatConfig();
            return _cached;
        }

        public static void Save(ChatConfig config)
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

        public static bool HasApiKey()
        {
            var config = Load();
            return !string.IsNullOrWhiteSpace(config.ApiKey);
        }

        public static void InvalidateCache() => _cached = null;
    }
}
