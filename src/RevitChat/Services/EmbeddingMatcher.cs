using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChat.Services
{
    /// <summary>
    /// Semantic matching using embeddings from Ollama.
    /// Compares user prompts against stored successful interactions using cosine similarity,
    /// providing much deeper understanding than keyword matching alone.
    /// Falls back gracefully when embedding model is unavailable.
    /// </summary>
    public sealed class EmbeddingMatcher : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private string _ollamaBaseUrl;
        private string _embeddingModel;
        private bool _available;
        private readonly object _lock = new();
        private List<EmbeddingEntry> _entries = new();
        private string _filePath;
        private const int MaxEntries = 500;
        private const int EmbeddingDim = 768; // typical for nomic-embed-text / mxbai-embed-large
        private const float MinSimilarityThreshold = 0.5f;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public bool IsAvailable => _available;

        public void Initialize(string dllDirectory, string ollamaBaseUrl, string embeddingModel = null)
        {
            _ollamaBaseUrl = (ollamaBaseUrl ?? "http://localhost:11434").TrimEnd('/');
            _embeddingModel = embeddingModel ?? "nomic-embed-text";

            var dir = Path.Combine(dllDirectory, "Data", "Feedback");
            Directory.CreateDirectory(dir);
            var user = SanitizeFileName(Environment.UserName);
            _filePath = Path.Combine(dir, $"embeddings_{user}.json");
            Load();

            _ = CheckAvailabilityAsync();
        }

        private async Task CheckAvailabilityAsync()
        {
            try
            {
                var testEmb = await GetEmbeddingAsync("test", CancellationToken.None);
                _available = testEmb != null && testEmb.Length > 0;
            }
            catch
            {
                _available = false;
            }
        }

        /// <summary>
        /// Get embedding vector for a prompt from Ollama's /api/embeddings endpoint.
        /// </summary>
        public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(_ollamaBaseUrl))
                return null;

            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    model = _embeddingModel,
                    prompt = text
                });

                var response = await _http.PostAsync(
                    $"{_ollamaBaseUrl}/api/embeddings",
                    new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
                    ct);

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("embedding", out var embProp))
                {
                    return embProp.EnumerateArray()
                        .Select(e => (float)e.GetDouble())
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EmbeddingMatcher] GetEmbedding: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Store a successful prompt with its embedding for future matching.
        /// </summary>
        public async Task StoreAsync(string prompt, string toolName,
            Dictionary<string, object> args, string intent, CancellationToken ct)
        {
            if (!_available || string.IsNullOrWhiteSpace(prompt)) return;

            var embedding = await GetEmbeddingAsync(prompt, ct);
            if (embedding == null) return;

            lock (_lock)
            {
                var existing = _entries.FirstOrDefault(e =>
                    string.Equals(e.ToolName, toolName, StringComparison.OrdinalIgnoreCase) &&
                    CosineSimilarity(e.Embedding, embedding) > 0.92f);

                if (existing != null)
                {
                    existing.UseCount++;
                    existing.LastUsed = DateTime.UtcNow.ToString("o");
                }
                else
                {
                    _entries.Add(new EmbeddingEntry
                    {
                        Prompt = prompt,
                        ToolName = toolName,
                        Args = SerializeArgs(args),
                        Intent = intent,
                        Embedding = embedding,
                        UseCount = 1,
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                        LastUsed = DateTime.UtcNow.ToString("o")
                    });

                    if (_entries.Count > MaxEntries)
                    {
                        _entries = _entries
                            .OrderByDescending(e => e.UseCount)
                            .Take(MaxEntries)
                            .ToList();
                    }
                }
            }

            SaveDebounced();
        }

        /// <summary>
        /// Find the most semantically similar stored prompts.
        /// Returns matched entries with similarity scores.
        /// </summary>
        public async Task<List<(EmbeddingEntry entry, float similarity)>> FindSimilarAsync(
            string prompt, int topK = 3, CancellationToken ct = default)
        {
            if (!_available || string.IsNullOrWhiteSpace(prompt))
                return new();

            var queryEmb = await GetEmbeddingAsync(prompt, ct);
            if (queryEmb == null) return new();

            lock (_lock)
            {
                return _entries
                    .Select(e => (entry: e, sim: CosineSimilarity(queryEmb, e.Embedding)))
                    .Where(x => x.sim >= MinSimilarityThreshold)
                    .OrderByDescending(x => x.sim)
                    .Take(topK)
                    .ToList();
            }
        }

        /// <summary>
        /// Find similar and format as few-shot examples.
        /// </summary>
        public async Task<List<string>> GetSemanticExamplesAsync(
            string prompt, int topK = 2, CancellationToken ct = default)
        {
            var matches = await FindSimilarAsync(prompt, topK, ct);
            return matches.Select(m =>
            {
                var argsStr = m.entry.Args.Count > 0
                    ? JsonSerializer.Serialize(m.entry.Args)
                    : "{}";
                return $"User: {m.entry.Prompt}\n<tool_call>\n{{\"name\": \"{m.entry.ToolName}\", \"arguments\": {argsStr}}}\n</tool_call>";
            }).ToList();
        }

        public int EntryCount
        {
            get { lock (_lock) { return _entries.Count; } }
        }

        /// <summary>
        /// Get tool recommendations by aggregating similarity scores across all stored entries.
        /// Returns a dictionary of tool_name -> cumulative weighted similarity score.
        /// This enables real-time learning: as new successful interactions are stored,
        /// future prompts instantly benefit from the expanded knowledge base.
        /// </summary>
        public Dictionary<string, double> GetToolRecommendations(float[] queryEmbedding, int minUseCount = 1)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in _entries)
                {
                    if (entry.UseCount < minUseCount) continue;

                    var sim = CosineSimilarity(queryEmbedding, entry.Embedding);
                    if (sim < MinSimilarityThreshold) continue;

                    var usageBoost = 1.0 + Math.Log(entry.UseCount + 1) * 0.2;
                    var weightedSim = sim * usageBoost;

                    scores.TryGetValue(entry.ToolName, out double current);
                    scores[entry.ToolName] = Math.Max(current, weightedSim);

                    counts.TryGetValue(entry.ToolName, out int cnt);
                    counts[entry.ToolName] = cnt + 1;
                }

                // Boost tools that matched multiple stored prompts
                foreach (var tool in counts.Keys.ToList())
                {
                    if (counts[tool] >= 2 && scores.ContainsKey(tool))
                        scores[tool] *= 1.0 + counts[tool] * 0.05;
                }

                return scores;
            }
        }

        /// <summary>
        /// Get tool recommendations asynchronously by first fetching the embedding.
        /// </summary>
        public async Task<Dictionary<string, double>> GetToolRecommendationsAsync(
            string prompt, int minUseCount = 1, CancellationToken ct = default)
        {
            if (!_available || string.IsNullOrWhiteSpace(prompt))
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            var embedding = await GetEmbeddingAsync(prompt, ct);
            if (embedding == null)
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            return GetToolRecommendations(embedding, minUseCount);
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0;
            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            float denom = (float)(Math.Sqrt(magA) * Math.Sqrt(magB));
            return denom < 1e-8f ? 0 : dot / denom;
        }

        private static Dictionary<string, object> SerializeArgs(Dictionary<string, object> args)
        {
            if (args == null) return new();
            var result = new Dictionary<string, object>();
            foreach (var kvp in args)
            {
                if (kvp.Value is JsonElement je)
                {
                    result[kvp.Key] = je.ValueKind switch
                    {
                        JsonValueKind.String => je.GetString(),
                        JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => je.GetRawText()
                    };
                }
                else result[kvp.Key] = kvp.Value;
            }
            return result;
        }

        private DateTime _lastSave = DateTime.MinValue;
        private void SaveDebounced()
        {
            if ((DateTime.UtcNow - _lastSave).TotalSeconds < 30) return;
            _lastSave = DateTime.UtcNow;
            Save();
        }

        internal void Save()
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(_filePath)) return;
                    File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, JsonOpts));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EmbeddingMatcher] Save: {ex.Message}");
                }
            }
        }

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
                    {
                        var json = File.ReadAllText(_filePath);
                        _entries = JsonSerializer.Deserialize<List<EmbeddingEntry>>(json) ?? new();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EmbeddingMatcher] Load: {ex.Message}");
                    _entries = new();
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "default";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.ToLowerInvariant();
        }

        public void Dispose()
        {
            Save();
            _http?.Dispose();
        }
    }

    public class EmbeddingEntry
    {
        [JsonPropertyName("prompt")] public string Prompt { get; set; }
        [JsonPropertyName("tool")] public string ToolName { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object> Args { get; set; } = new();
        [JsonPropertyName("intent")] public string Intent { get; set; }
        [JsonPropertyName("embedding")] public float[] Embedding { get; set; }
        [JsonPropertyName("use_count")] public int UseCount { get; set; }
        [JsonPropertyName("created")] public string CreatedAt { get; set; }
        [JsonPropertyName("last_used")] public string LastUsed { get; set; }
    }
}
