using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChatLocal.Services
{
    public class ModelInfo
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public bool IsInstalled { get; set; }
        public string DisplayName => IsInstalled ? $"{Name}  [{Size}]" : Name;
    }

    public static class OllamaModelService
    {
        private static readonly HttpClient _httpQuick = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly HttpClient _httpPull = new() { Timeout = TimeSpan.FromMinutes(60) };

        public static readonly List<string> RecommendedModels = new()
        {
            "qwen3:8b",
            "qwen3:14b",
            "qwen3:32b",
            "qwen2.5:7b",
            "qwen2.5:14b",
            "qwen2.5:32b",
            "qwen2.5-coder:7b",
            "qwen2.5-coder:14b",
            "llama3.1:8b",
            "llama3.1:70b",
            "mistral:7b",
            "gemma2:9b",
            "phi3:14b",
            "deepseek-r1:7b",
            "deepseek-r1:14b",
        };

        public static async Task<List<ModelInfo>> GetInstalledModelsAsync(string endpointUrl, CancellationToken ct = default)
        {
            var results = new List<ModelInfo>();
            try
            {
                var baseUrl = endpointUrl.TrimEnd('/');
                var response = await _httpQuick.GetAsync($"{baseUrl}/api/tags", ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        var name = m.GetProperty("name").GetString() ?? "";
                        long sizeBytes = 0;
                        if (m.TryGetProperty("size", out var sizeProp))
                            sizeBytes = sizeProp.GetInt64();

                        results.Add(new ModelInfo
                        {
                            Name = name,
                            Size = FormatSize(sizeBytes),
                            IsInstalled = true
                        });
                    }
                }
            }
            catch
            {
            }
            return results;
        }

        public static async Task<List<ModelInfo>> GetModelListWithStatusAsync(string endpointUrl, CancellationToken ct = default)
        {
            var installed = await GetInstalledModelsAsync(endpointUrl, ct);
            var installedNames = new HashSet<string>(installed.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

            var result = new List<ModelInfo>();

            foreach (var m in installed)
                result.Add(m);

            foreach (var name in RecommendedModels)
            {
                if (!installedNames.Contains(name))
                    result.Add(new ModelInfo { Name = name, Size = "", IsInstalled = false });
            }

            return result;
        }

        public static async Task PullModelAsync(
            string endpointUrl,
            string modelName,
            Action<string> onProgress,
            CancellationToken ct = default)
        {
            var baseUrl = endpointUrl.TrimEnd('/');
            var requestBody = JsonSerializer.Serialize(new { name = modelName, stream = true });
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/pull")
            {
                Content = content
            };

            using var response = await _httpPull.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody;
                try { errorBody = await response.Content.ReadAsStringAsync(); }
                catch { errorBody = response.ReasonPhrase; }
                throw new HttpRequestException(
                    $"Ollama pull failed ({(int)response.StatusCode}): {errorBody}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errProp))
                    {
                        var errMsg = errProp.GetString();
                        throw new InvalidOperationException($"Ollama error: {errMsg}");
                    }

                    var status = root.TryGetProperty("status", out var sp) ? sp.GetString() : "";

                    if (root.TryGetProperty("total", out var totalProp) &&
                        root.TryGetProperty("completed", out var completedProp))
                    {
                        long total = totalProp.GetInt64();
                        long completed = completedProp.GetInt64();
                        if (total > 0)
                        {
                            int pct = (int)(completed * 100 / total);
                            onProgress?.Invoke($"{status} {pct}% ({FormatSize(completed)}/{FormatSize(total)})");
                        }
                        else
                        {
                            onProgress?.Invoke(status);
                        }
                    }
                    else
                    {
                        onProgress?.Invoke(status);
                    }
                }
                catch (JsonException)
                {
                    onProgress?.Invoke(line);
                }
            }

            onProgress?.Invoke("Done");
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F0} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F0} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }
}
