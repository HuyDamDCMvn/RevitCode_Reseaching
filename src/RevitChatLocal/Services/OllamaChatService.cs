using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using RevitChat.Skills;

using OaiMessage = OpenAI.Chat.ChatMessage;

namespace RevitChatLocal.Services
{
    public class OllamaChatService : RevitChat.Models.IChatService, RevitChat.Models.IEmbeddingCapable
    {
        private ChatClient _client;
        private readonly List<OaiMessage> _conversationHistory = new();
        private readonly SkillRegistry _skillRegistry;
        private HashSet<string> _allToolNames;
        private string _lastUserMessage = "";
        private bool _isContinuation;
        private RevitChat.Services.EmbeddingMatcher _embeddingMatcher;
        private string _ollamaBaseUrl;

        public event Action<string> DebugMessage;
        public event Action<string> TokenReceived;

        private string _toolMode = "smart";
        private List<string> _enabledPacks = new()
        {
            "Core", "ViewControl", "MEP", "Modeler", "BIMCoordinator", "LinkedModels"
        };

        private string _cachedSystemPrompt;
        private string _cachedPromptKeywords;

        #region CoreTools + KeywordGroups (for Smart mode)

        private class KeywordGroup
        {
            public string Name { get; init; }
            public string[] Keywords { get; init; }
            public string[] Tools { get; init; }
            public int Weight { get; init; } = 1;
        }

        #region Static Data (loaded from JSON)

        private static readonly string _chatConfigDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(OllamaChatService).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "Data", "ChatConfig");

        private static HashSet<string> CoreTools;
        private static KeywordGroup[] KeywordGroups;
        private static List<(string from, string to)> NormalizationMap;
        private static HashSet<string> ActionKeywords;
        private static Dictionary<string, string> ToolSchemaHints;
        private static List<(string[] keywords, string example)> FewShotExamples;
        private static HashSet<string> ChitchatPatterns;
        private static string SmartTagKnowledgeSummary;
        private static bool _dataLoaded;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _fewShotCache = new();
        private const int FewShotCacheMaxSize = 32;
        private static readonly object _dataLoadLock = new();

        private static void EnsureDataLoaded()
        {
            if (_dataLoaded) return;
            lock (_dataLoadLock)
            {
                if (_dataLoaded) return;
                LoadAllData();
                _dataLoaded = true;
            }
        }

        private static T LoadJson<T>(string fileName)
        {
            var path = System.IO.Path.Combine(_chatConfigDir, fileName);
            if (!System.IO.File.Exists(path))
                throw new System.IO.FileNotFoundException($"Chat config not found: {path}");
            var json = System.IO.File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }

        private static void LoadAllData()
        {
            // keyword_groups.json
            var kgDoc = LoadJson<System.Text.Json.JsonElement>("keyword_groups.json");

            CoreTools = new HashSet<string>();
            foreach (var item in kgDoc.GetProperty("core_tools").EnumerateArray())
            {
                var val = item.GetString();
                if (val != null) CoreTools.Add(val);
            }

            var groupsList = new List<KeywordGroup>();
            foreach (var g in kgDoc.GetProperty("groups").EnumerateArray())
            {
                var keywords = new List<string>();
                foreach (var k in g.GetProperty("keywords").EnumerateArray())
                {
                    var kv = k.GetString();
                    if (kv != null) keywords.Add(kv);
                }
                var tools = new List<string>();
                foreach (var t in g.GetProperty("tools").EnumerateArray())
                {
                    var tv = t.GetString();
                    if (tv != null) tools.Add(tv);
                }
                groupsList.Add(new KeywordGroup
                {
                    Name = g.GetProperty("name").GetString() ?? "",
                    Keywords = keywords.ToArray(),
                    Tools = tools.ToArray(),
                    Weight = g.TryGetProperty("weight", out var w) ? w.GetInt32() : 1
                });
            }
            KeywordGroups = groupsList.ToArray();

            ActionKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in kgDoc.GetProperty("action_keywords").EnumerateArray())
            {
                var val = item.GetString();
                if (val != null) ActionKeywords.Add(val);
            }

            // tool_schema_hints.json
            ToolSchemaHints = LoadJson<Dictionary<string, string>>("tool_schema_hints.json")
                ?? new Dictionary<string, string>();

            // fewshot_examples.json
            var fsDoc = LoadJson<System.Text.Json.JsonElement>("fewshot_examples.json");
            FewShotExamples = new List<(string[] keywords, string example)>();
            foreach (var item in fsDoc.EnumerateArray())
            {
                var kws = new List<string>();
                foreach (var k in item.GetProperty("keywords").EnumerateArray())
                {
                    var kv = k.GetString();
                    if (kv != null) kws.Add(kv);
                }
                var ex = item.GetProperty("example").GetString();
                if (kws.Count > 0 && ex != null)
                    FewShotExamples.Add((kws.ToArray(), ex));
            }

            // chat_normalization.json
            var cnDoc = LoadJson<System.Text.Json.JsonElement>("chat_normalization.json");

            NormalizationMap = new List<(string from, string to)>();
            foreach (var item in cnDoc.GetProperty("normalization_map").EnumerateArray())
            {
                var fromVal = item.GetProperty("from").GetString();
                var toVal = item.GetProperty("to").GetString();
                if (fromVal != null && toVal != null)
                    NormalizationMap.Add((fromVal, toVal));
            }

            ChitchatPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in cnDoc.GetProperty("chitchat_patterns").EnumerateArray())
                ChitchatPatterns.Add(item.GetString());

            // smarttag_knowledge.json — build a compact summary for system prompt injection
            SmartTagKnowledgeSummary = "";
            try
            {
                var stPath = System.IO.Path.Combine(_chatConfigDir, "smarttag_knowledge.json");
                if (System.IO.File.Exists(stPath))
                {
                    var stDoc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(stPath));
                    var root = stDoc.RootElement;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("[Tagging Knowledge from SmartTag]");
                    if (root.TryGetProperty("tag_format_patterns", out var patterns))
                    {
                        sb.AppendLine("Tag format patterns:");
                        foreach (var p in patterns.EnumerateArray())
                        {
                            var pat = p.TryGetProperty("pattern", out var pv) ? pv.GetString() : "";
                            var use = p.TryGetProperty("use_for", out var uv) ? uv.GetString() : "";
                            sb.AppendLine($"  - {pat} ({use})");
                        }
                    }
                    if (root.TryGetProperty("tagging_guidance", out var guidance)
                        && guidance.TryGetProperty("general_rules", out var rules))
                    {
                        sb.AppendLine("Tagging rules:");
                        foreach (var r in rules.EnumerateArray())
                            sb.AppendLine($"  - {r.GetString()}");
                    }
                    SmartTagKnowledgeSummary = sb.ToString().Trim();
                }
            }
            catch { }
        }

        #endregion

        #region Dynamic Few-Shot Examples

        private const int DefaultFewShotExamples = 5;
        private const int ComplexFewShotExamples = 7;

        private string BuildDynamicExamples(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return "";

            var normalized = NormalizeForMatching(userMessage);

            if (_fewShotCache.TryGetValue(normalized, out var cached))
                return cached;

            var lower = userMessage.ToLowerInvariant();
            var scored = new List<(int score, string example)>();

            foreach (var (keywords, example) in FewShotExamples)
            {
                int score = 0;
                foreach (var kw in keywords)
                {
                    if (MatchesKeyword(lower, kw) || MatchesKeyword(normalized, kw))
                        score++;
                }
                if (score > 0)
                    scored.Add((score, example));
            }

            var limit = GetFewShotLimit(userMessage);

            try
            {
                var approved = RevitChat.Services.ChatFeedbackService.GetSimilarApproved(userMessage, 2);
                foreach (var a in approved)
                {
                    if (a.Tools == null || a.Tools.Count == 0) continue;
                    var tool = a.Tools[0];
                    var argsJson = a.Tools.Count == 1 && tool.Args?.Count > 0
                        ? System.Text.Json.JsonSerializer.Serialize(tool.Args)
                        : "{}";
                    var example = $"User: {a.Prompt}\nAssistant:\n<tool_call>\n{{\"name\": \"{tool.Name}\", \"arguments\": {argsJson}}}\n</tool_call>";
                    scored.Add((100 + a.UseCount, example));
                }
            }
            catch { }

            // Phase 2b: Dynamic learned examples from successful interactions
            try
            {
                var ctx = RevitChat.Services.PromptAnalyzer.Analyze(userMessage);
                var dynamicExamples = RevitChat.Services.DynamicFewShotSelector.GetDynamicExamples(
                    userMessage, 2, ctx.PrimaryIntent.ToString(), ctx.DetectedCategory);
                foreach (var de in dynamicExamples)
                    scored.Add((80, de));
            }
            catch { }

            if (scored.Count == 0)
            {
                CacheFewShotResult(normalized, "");
                return "";
            }

            var selected = scored
                .OrderByDescending(s => s.score)
                .Take(limit)
                .Select(s => s.example);

            var sb = new StringBuilder();
            foreach (var ex in selected)
            {
                sb.AppendLine(ex);
                sb.AppendLine();
            }
            var result = sb.ToString();
            CacheFewShotResult(normalized, result);
            return result;
        }

        private static void CacheFewShotResult(string key, string value)
        {
            if (_fewShotCache.Count >= FewShotCacheMaxSize)
                _fewShotCache.Clear();
            _fewShotCache.TryAdd(key, value);
        }

        #endregion

        #endregion

        private static string NormalizeForMatching(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var text = input.ToLowerInvariant();
            foreach (var (from, to) in NormalizationMap)
            {
                if (from == null || to == null) continue;
                text = text.Replace(from, to);
            }
            var stripped = StripVietnameseDiacritics(text);
            foreach (var (from, to) in NormalizationMap)
            {
                if (from == null || to == null) continue;
                stripped = stripped.Replace(StripVietnameseDiacritics(from), to);
            }
            return stripped;
        }

        private static string StripVietnameseDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            var result = sb.ToString().Normalize(NormalizationForm.FormC);
            return result.Replace('đ', 'd').Replace('Đ', 'D');
        }

        private static bool ContainsActionVerb(string normalizedText)
        {
            foreach (var kw in ActionKeywords)
            {
                if (MatchesKeyword(normalizedText, kw))
                    return true;
            }
            return false;
        }

        private static int GetFewShotLimit(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return DefaultFewShotExamples;
            if (ShouldUseTwoStageInternal(userMessage)) return ComplexFewShotExamples;
            if (userMessage.Length > 140) return ComplexFewShotExamples;
            var commaCount = userMessage.Count(c => c == ',' || c == ';');
            return commaCount >= 2 ? ComplexFewShotExamples : DefaultFewShotExamples;
        }

        private static bool ShouldUseTwoStageInternal(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return false;
            if (userMessage.Length < 40) return false;
            var text = NormalizeForMatching(userMessage);
            int actionHits = 0;
            foreach (var kw in ActionKeywords)
            {
                if (MatchesKeyword(text, kw))
                    actionHits++;
            }

            if (actionHits >= 3) return true;

            var explicitSeparators = new[] { " then ", " sau đó ", " rồi ", "->", " after that " };
            return actionHits >= 2 && explicitSeparators.Any(s => text.Contains(s));
        }

        private static Regex GetOrCreateRegex(string word)
        {
            return _regexCache.GetOrAdd(word, k =>
                new Regex($@"\b{Regex.Escape(k)}\w*\b",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase));
        }

        private static bool MatchesKeyword(string text, string kw)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(kw)) return false;
            if (MatchesKeywordCore(text, kw))
                return true;
            var strippedKw = StripVietnameseDiacritics(kw);
            if (strippedKw != kw && MatchesKeywordCore(text, strippedKw))
                return true;
            return false;
        }

        private static bool MatchesKeywordCore(string text, string kw)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(kw)) return false;
            if (kw.Contains(' '))
            {
                var parts = kw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.All(p => GetOrCreateRegex(p).IsMatch(text));
            }
            return GetOrCreateRegex(kw).IsMatch(text);
        }

        private static List<(KeywordGroup group, int score)> GetMatchedGroups(string normalizedText)
        {
            var matches = new List<(KeywordGroup group, int score)>();
            foreach (var group in KeywordGroups)
            {
                int count = 0;
                int totalKeywordChars = 0;
                foreach (var kw in group.Keywords)
                {
                    if (MatchesKeyword(normalizedText, kw))
                    {
                        count++;
                        totalKeywordChars += kw.Length;
                    }
                }
                if (count > 0)
                {
                    int multiMatchBonus = count >= 3 ? 3 : (count >= 2 ? 1 : 0);
                    int charBonus = totalKeywordChars / 5;
                    int score = (count * group.Weight) + multiMatchBonus + charBonus;
                    matches.Add((group, score));
                }
            }
            return matches;
        }

        private bool ShouldAskDisambiguation(string userMessage, out List<KeywordGroup> groups)
        {
            groups = new List<KeywordGroup>();
            if (string.IsNullOrWhiteSpace(userMessage)) return false;

            var normalized = NormalizeForMatching(userMessage);
            var matches = GetMatchedGroups(normalized)
                .OrderByDescending(m => m.score)
                .ToList();

            if (matches.Count < 2) return false;
            if (ContainsActionVerb(normalized)) return false;

            groups = matches.Take(3).Select(m => m.group).ToList();
            return true;
        }

        private string BuildDisambiguationQuestion(string userMessage, List<KeywordGroup> groups)
        {
            var names = groups.Select(g => g.Name).ToList();
            var hint = names.Count > 0 ? string.Join(", ", names) : "nhiều nhóm khác nhau";

            if (IsVietnamese(userMessage))
                return $"Mình chưa rõ bạn muốn làm gì. Yêu cầu có thể liên quan tới: {hint}. Bạn muốn thao tác nào (đếm, liệt kê, ẩn/hiện, đổi màu, xuất dữ liệu...)?";

            return $"I’m not sure what action you want. Your request could relate to: {hint}. Please specify the action (count, list, hide/show, override color, export...).";
        }

        private static bool IsVietnamese(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Any(ch => ch >= 0x00C0 && ch <= 0x1EF9);
        }

        private static bool IsChitchat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var clean = text.Trim().TrimEnd('?', '!', '.').Trim().ToLowerInvariant();
            if (clean.Length > 80) return false;
            if (ChitchatPatterns.Contains(clean)) return true;
            if (clean.Length <= 3 && !Regex.IsMatch(clean, @"\d")) return true;
            return false;
        }

        private async Task<(string, List<RevitChat.Models.ToolCallRequest>)> GetChitchatResponseAsync(
            CancellationToken ct)
        {
            var config = LocalConfigService.Load();
            var options = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };

            var messages = new List<OaiMessage>
            {
                new SystemChatMessage(
                    "You are a friendly Revit BIM assistant. Answer the user's greeting or question briefly. " +
                    "If they ask what you can do, list: query/count/search elements, color override by system/level/filter, " +
                    "isolate/hide elements, export to CSV, tag elements, check MEP systems, validate models, " +
                    "detect clashes, manage sheets/views, create 3D views, modify parameters, " +
                    "measure distances, check insulation, generate BOQ, audit naming conventions, " +
                    "and 170+ more specialized BIM operations. " +
                    "Understand Vietnamese, English, and mixed-language prompts. Reply in the language the user primarily uses. " +
                    "Do NOT output any <tool_call> tags. " +
                    "If the user asks something you cannot help with, reply: \"This matter haven't yet train, Please contact your Digital Lead to update me.\"")
            };
            messages.AddRange(_conversationHistory);

            var text = await StreamCompletionAsync(messages, options, ct);
            text = RemoveToolCallTags(text).Trim();
            _conversationHistory.Add(new AssistantChatMessage(text));
            return (text, new List<RevitChat.Models.ToolCallRequest>());
        }


        public OllamaChatService(SkillRegistry skillRegistry)
        {
            EnsureDataLoaded();
            _skillRegistry = skillRegistry;
            RebuildAllToolNames();
        }

        public void SetToolMode(string mode)
        {
            _toolMode = mode ?? "smart";
        }

        public void SetEnabledPacks(List<string> packs)
        {
            _enabledPacks = packs ?? new List<string> { "Core" };
            if (!_enabledPacks.Contains("Core"))
                _enabledPacks.Insert(0, "Core");
            RebuildAllToolNames();
        }

        private void RebuildAllToolNames()
        {
            var tools = _skillRegistry.GetToolDefinitionsByPacks(_enabledPacks);
            _allToolNames = new HashSet<string>();
            foreach (var t in tools)
                _allToolNames.Add(t.FunctionName);
        }

        public void Initialize(string endpointUrl, string model)
        {
            if (string.IsNullOrWhiteSpace(endpointUrl))
                throw new ArgumentException("Endpoint URL cannot be empty.", nameof(endpointUrl));
            _ollamaBaseUrl = endpointUrl.TrimEnd('/');
            var endpoint = _ollamaBaseUrl;
            if (!endpoint.EndsWith("/v1"))
                endpoint += "/v1";
            endpoint += "/";

            var options = new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                NetworkTimeout = TimeSpan.FromMinutes(5)
            };
            var client = new OpenAIClient(new ApiKeyCredential("ollama"), options);
            _client = client.GetChatClient(model);

            // Phase 4: Initialize embedding matcher (async, non-blocking)
            try
            {
                var dllDir = System.IO.Path.GetDirectoryName(typeof(OllamaChatService).Assembly.Location)
                            ?? AppDomain.CurrentDomain.BaseDirectory;
                _embeddingMatcher = new RevitChat.Services.EmbeddingMatcher();
                _embeddingMatcher.Initialize(dllDir, _ollamaBaseUrl);
            }
            catch { _embeddingMatcher = null; }
        }

        public bool IsInitialized => _client != null;

        public void ClearHistory()
        {
            _conversationHistory.Clear();
            _lastUserMessage = "";
            _cachedSystemPrompt = null;
            _cachedPromptKeywords = null;
        }

        public void RepairHistoryAfterCancel()
        {
            if (_conversationHistory.Count > 0 &&
                _conversationHistory.Last() is AssistantChatMessage am)
            {
                var text = am.Content?.FirstOrDefault()?.Text ?? "";
                if (text.Contains("(tool call)") || text.Contains("<tool_call>"))
                {
                    _conversationHistory.RemoveAt(_conversationHistory.Count - 1);
                    _conversationHistory.Add(new AssistantChatMessage("(cancelled by user)"));
                }
            }
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default)
        {
            if (_client == null)
                throw new InvalidOperationException("Ollama client not initialized.");

            _lastUserMessage = userMessage;
            _isContinuation = false;
            _conversationHistory.Add(new UserChatMessage(userMessage));
            TrimHistory();

            if (IsChitchat(userMessage))
            {
                return await GetChitchatResponseAsync(ct);
            }

            if (ShouldAskDisambiguation(userMessage, out var groups))
            {
                var question = BuildDisambiguationQuestion(userMessage, groups);
                _conversationHistory.Add(new AssistantChatMessage(question));
                return (question, new List<RevitChat.Models.ToolCallRequest>());
            }

            try
            {
                var feedbackOverride = RevitChat.Services.ChatFeedbackService.FindPromptOverride(userMessage);
                if (feedbackOverride.HasValue)
                {
                    var (toolName, args) = feedbackOverride.Value;
                    if (_allToolNames.Contains(toolName))
                    {
                        var toolCall = new RevitChat.Models.ToolCallRequest
                        {
                            ToolCallId = $"fb_{Guid.NewGuid():N}",
                            FunctionName = toolName,
                            Arguments = args ?? new Dictionary<string, object>()
                        };
                        var hint = $"(matched from feedback history: {toolName})";
                        _conversationHistory.Add(new AssistantChatMessage(hint));
                        return (hint, new List<RevitChat.Models.ToolCallRequest> { toolCall });
                    }
                }
            }
            catch { }

            // Phase 4: Inject semantic examples from embedding matcher (async, non-blocking)
            if (_embeddingMatcher?.IsAvailable == true)
            {
                try
                {
                    var semanticExamples = await _embeddingMatcher.GetSemanticExamplesAsync(userMessage, 2, ct);
                    if (semanticExamples.Count > 0)
                    {
                        var embHint = "[Semantic Examples from history]\n" + string.Join("\n\n", semanticExamples);
                        _conversationHistory.Add(new SystemChatMessage(embHint));
                    }
                }
                catch { }
            }

            return await GetCompletionAsync(ct);
        }

        /// <summary>
        /// Store a successful tool call interaction in the embedding matcher for future semantic retrieval.
        /// </summary>
        public async Task StoreEmbeddingAsync(string prompt, string toolName,
            Dictionary<string, object> args, string intent, CancellationToken ct = default)
        {
            if (_embeddingMatcher?.IsAvailable != true) return;
            try { await _embeddingMatcher.StoreAsync(prompt, toolName, args, intent, ct); }
            catch { }
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            if (toolResults == null) toolResults = new Dictionary<string, string>();
            var sb = new StringBuilder(256);
            sb.AppendLine($"User asked: \"{_lastUserMessage}\"");
            sb.AppendLine("Results:");
            int totalChars = 0;
            foreach (var kvp in toolResults)
            {
                var val = kvp.Value ?? "";
                totalChars += val.Length;
                sb.AppendLine($"[{kvp.Key}]: {val}");
            }

            if (totalChars > 6000)
                sb.AppendLine("Data is large. Summarize for user. Don't repeat raw data.");

            sb.AppendLine("Now answer the user in natural language based on the results above.");
            sb.AppendLine("Do NOT output (tool call) or any tool syntax. Write a clear human-readable answer.");
            sb.AppendLine("If more steps are needed, output exactly ONE <tool_call> block instead.");

            _conversationHistory.Add(new UserChatMessage(sb.ToString()));

            _isContinuation = true;
            try
            {
                return await GetCompletionAsync(ct);
            }
            finally
            {
                _isContinuation = false;
            }
        }

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionAsync(
            CancellationToken ct)
        {
            if (!_isContinuation)
            {
                if (_toolMode == "twostage")
                    return await GetCompletionTwoStageAsync(ct);

                if (_toolMode == "smart" && ShouldUseTwoStageInternal(_lastUserMessage))
                {
                    DebugMessage?.Invoke("Auto Two-Stage enabled for complex prompt.");
                    return await GetCompletionTwoStageAsync(ct);
                }
            }

            return await GetCompletionWithRetryAsync(ct);
        }

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionWithRetryAsync(
            CancellationToken ct)
        {
            var result = await GetCompletionOnceAsync(ct);
            if (!result.shouldRetry) return result.parsed;

            var retry = await GetCompletionOnceAsync(ct, retryHint: true);
            if (!retry.shouldRetry) return retry.parsed;

            var fallback = BuildFallbackSuggestion(_lastUserMessage);
            _conversationHistory.Add(new AssistantChatMessage(fallback));
            return (fallback, new List<RevitChat.Models.ToolCallRequest>());
        }

        private async Task<string> StreamCompletionAsync(
            List<OaiMessage> messages, ChatCompletionOptions options,
            CancellationToken ct, bool emitTokens = true)
        {
            var sb = new StringBuilder(512);
            var toolCallDetected = false;
            var recentChars = new char[32];
            int recentPos = 0;

            await foreach (var update in _client.CompleteChatStreamingAsync(messages, options, ct))
            {
                foreach (var part in update.ContentUpdate)
                {
                    var token = part.Text;
                    if (token == null) continue;
                    sb.Append(token);

                    if (!toolCallDetected && emitTokens)
                    {
                        foreach (var ch in token)
                        {
                            recentChars[recentPos % 32] = ch;
                            recentPos++;
                        }

                        if (token.Contains('<') || token.Contains('{'))
                        {
                            var full = sb.ToString();
                            if (full.Contains("<tool_call>") || full.Contains("{\"name\""))
                            {
                                toolCallDetected = true;
                                continue;
                            }
                        }
                        TokenReceived?.Invoke(token);
                    }
                }
            }

            return StripQwenTokens(sb.ToString());
        }

        private async Task<(bool shouldRetry, (string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls) parsed)> GetCompletionOnceAsync(
            CancellationToken ct, bool retryHint = false)
        {
            var config = LocalConfigService.Load();
            var options = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };

            var messages = BuildMessages();
            if (retryHint)
            {
                messages.Add(new UserChatMessage(
                    "Your last response was invalid. Output ONLY one <tool_call> or a direct answer. " +
                    "Do NOT use code fences or extra text."));
            }

            var text = await StreamCompletionAsync(messages, options, ct);

            var parsed = ParseResponse(text, out var cleanText, out var toolCalls);
            var looksLikeToolCall = LooksLikeToolCall(text);

            if (looksLikeToolCall && toolCalls.Count == 0)
                return (true, (null, new List<RevitChat.Models.ToolCallRequest>()));

            AddToHistory(parsed, cleanText, toolCalls);
            return (false, parsed);
        }

        #region Two-Stage Mode

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionTwoStageAsync(
            CancellationToken ct)
        {
            var config = LocalConfigService.Load();
            var options = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };

            var stage1Messages = BuildTwoStageSelectionMessages();
            var stage1Text = await StreamCompletionAsync(stage1Messages, options, ct, emitTokens: false);

            var selectedTools = ParseSelectedToolNames(stage1Text);
            if (selectedTools.Count == 0)
                selectedTools = CoreTools.ToList();

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var stage2Messages = BuildMessages(selectedTools);
                if (attempt > 0)
                {
                    stage2Messages.Add(new UserChatMessage(
                        "Your last response was invalid. Output ONLY one <tool_call> or a direct answer. " +
                        "Do NOT use code fences or extra text."));
                }

                var stage2Text = await StreamCompletionAsync(stage2Messages, options, ct);

                var parsed = ParseResponse(stage2Text, out var cleanText, out var toolCalls);
                if (LooksLikeToolCall(stage2Text) && toolCalls.Count == 0)
                    continue;

                AddToHistory(parsed, cleanText, toolCalls);
                return parsed;
            }

            var fallback = BuildFallbackSuggestion(_lastUserMessage);
            _conversationHistory.Add(new AssistantChatMessage(fallback));
            return (fallback, new List<RevitChat.Models.ToolCallRequest>());
        }

        private List<OaiMessage> BuildTwoStageSelectionMessages()
        {
            var tools = _skillRegistry.GetToolDefinitionsByPacks(_enabledPacks);
            var toolList = new StringBuilder();
            foreach (var t in tools)
                toolList.AppendLine($"- {t.FunctionName}");

            var systemPrompt = $@"You are a tool selector for a Revit BIM assistant.
Given the user's request, select 5-10 tools from the list below that are most relevant.

## ALL AVAILABLE TOOLS
{toolList}

## OUTPUT FORMAT
Return ONLY a JSON array of tool names. No explanation, no extra text.

Example:
[""get_elements"", ""count_elements"", ""get_levels""]";

            var messages = new List<OaiMessage> { new SystemChatMessage(systemPrompt) };

            if (_conversationHistory.Count > 0)
            {
                var last = _conversationHistory.Last();
                messages.Add(last);
            }

            return messages;
        }

        private List<string> ParseSelectedToolNames(string text)
        {
            var results = new List<string>();
            try
            {
                var cleaned = text.Trim();
                // Strip markdown code fences if present
                cleaned = Regex.Replace(cleaned, @"^```(?:json)?\s*", "", RegexOptions.IgnoreCase);
                cleaned = Regex.Replace(cleaned, @"\s*```$", "");
                cleaned = cleaned.Trim();

                if (!cleaned.StartsWith("[")) return results;

                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrEmpty(name) && _allToolNames.Contains(name))
                            results.Add(name);
                    }
                }
            }
            catch
            {
                // Fallback: extract quoted strings
                var matches = Regex.Matches(text, @"""([a-z_]+)""");
                foreach (Match m in matches)
                {
                    var name = m.Groups[1].Value;
                    if (_allToolNames.Contains(name) && !results.Contains(name))
                        results.Add(name);
                }
            }

            return results;
        }

        #endregion

        #region Tool Catalog Builders

        private string BuildToolCatalogForMessage(string userMessage, List<string> forcedTools = null)
        {
            var packTools = _skillRegistry.GetToolDefinitionsByPacks(_enabledPacks);
            var toolIndex = new Dictionary<string, ChatTool>();
            foreach (var t in packTools)
                toolIndex[t.FunctionName] = t;

            HashSet<string> selected;

            if (forcedTools != null)
            {
                selected = new HashSet<string>(forcedTools);
            }
            else if (_toolMode == "showall")
            {
                selected = new HashSet<string>(toolIndex.Keys);
            }
            else
            {
                // Smart mode: CoreTools + keyword match + PromptAnalyzer
                selected = new HashSet<string>(CoreTools);
                var combined = string.IsNullOrWhiteSpace(_lastUserMessage) || _lastUserMessage == userMessage
                    ? userMessage
                    : $"{userMessage} {_lastUserMessage}";
                var normalized = NormalizeForMatching(combined);
                var matches = GetMatchedGroups(normalized);
                foreach (var match in matches)
                {
                    foreach (var toolName in match.group.Tools)
                        selected.Add(toolName);
                }

                var ctx = RevitChat.Services.PromptAnalyzer.Analyze(combined);
                foreach (var suggestedTool in ctx.SuggestedTools)
                {
                    if (toolIndex.ContainsKey(suggestedTool))
                        selected.Add(suggestedTool);
                }
            }

            const int maxToolsInCatalog = 25;
            var ordered = selected
                .Where(t => toolIndex.ContainsKey(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .Take(maxToolsInCatalog)
                .ToList();

            var sb = new StringBuilder(ordered.Count * 80);
            foreach (var toolName in ordered)
            {
                var tool = toolIndex[toolName];
                if (ToolSchemaHints.TryGetValue(tool.FunctionName, out var hint))
                    sb.AppendLine($"- {tool.FunctionName}({hint})");
                else
                    sb.AppendLine($"- {tool.FunctionName}: {tool.FunctionDescription}");
            }

            return sb.ToString();
        }

        #endregion

        private (string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls) ParseResponse(
            string text, out string cleanText, out List<RevitChat.Models.ToolCallRequest> toolCalls)
        {
            cleanText = "";
            toolCalls = ExtractToolCalls(text);
            if (toolCalls.Count > 0)
            {
                cleanText = RemoveToolCallTags(text).Trim();
                return (null, toolCalls);
            }

            var sanitized = SanitizeForDisplay(text);
            return (sanitized, new List<RevitChat.Models.ToolCallRequest>());
        }

        private static string SanitizeForDisplay(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var result = Regex.Replace(text, @"</?tool_call>", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\{[^{}]*""name""\s*:\s*""[a-z_]+""[^{}]*\}", "", RegexOptions.Singleline);
            result = Regex.Replace(result, @"""arguments""\s*:\s*\{(?:[^{}]|\{[^{}]*\})*\}", "", RegexOptions.Singleline);
            result = Regex.Replace(result, @"[A-Za-z]+:\s*""[a-z_]+""\s*,?\s*""arguments""", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"```json?\s*```", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"[\{\}\[\]"",:]+\s*$", "");
            result = result.Trim();
            return string.IsNullOrWhiteSpace(result) ? "" : result;
        }

        private void AddToHistory(
            (string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls) parsed,
            string cleanText,
            List<RevitChat.Models.ToolCallRequest> toolCalls)
        {
            if (toolCalls.Count > 0)
            {
                var toolNames = string.Join(", ", toolCalls.Select(t => t.FunctionName));
                var historyText = !string.IsNullOrEmpty(cleanText)
                    ? cleanText
                    : $"[Calling tool: {toolNames}]";
                _conversationHistory.Add(new AssistantChatMessage(historyText));
            }
            else
            {
                _conversationHistory.Add(new AssistantChatMessage(parsed.assistantMessage ?? ""));
            }
        }

        private string StripQwenTokens(string text)
        {
            text = Regex.Replace(text, @"<\|im_start\|>.*?(?:<\|im_end\|>|$)", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"<\|im_(?:start|end)\|>", "");
            return text;
        }

        private List<RevitChat.Models.ToolCallRequest> ExtractToolCalls(string text)
        {
            var results = new List<RevitChat.Models.ToolCallRequest>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            var stripped = Regex.Replace(text, @"```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            stripped = stripped.Replace("```", "");

            // 1) Well-formed <tool_call>...</tool_call> with balanced braces (handles nested JSON)
            var tagPattern = new Regex(
                @"<tool_call>\s*(\{(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*\})\s*</tool_call>",
                RegexOptions.Singleline);

            foreach (Match match in tagPattern.Matches(stripped))
            {
                var call = TryParseOneToolCall(match.Groups[1].Value);
                if (call != null) results.Add(call);
            }

            if (results.Count > 0) return results;

            // 2) Multiple JSON objects inside a single <tool_call> block (merged calls)
            var mergedPattern = new Regex(
                @"<tool_call>\s*(.*?)\s*</tool_call>",
                RegexOptions.Singleline);
            foreach (Match match in mergedPattern.Matches(stripped))
            {
                var inner = match.Groups[1].Value;
                var jsonObjects = Regex.Matches(inner,
                    @"\{(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*\}",
                    RegexOptions.Singleline);
                foreach (Match jm in jsonObjects)
                {
                    var call = TryParseOneToolCall(jm.Value);
                    if (call != null) results.Add(call);
                }
            }

            if (results.Count > 0) return results;

            // 3) JSON inside markdown fences
            var jsonBlockPattern = new Regex(
                @"```(?:json)?\s*(\{[^`]*?""name""\s*:\s*""[a-z_]+""\s*[^`]*?\})\s*```",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in jsonBlockPattern.Matches(text))
            {
                var call = TryParseOneToolCall(match.Groups[1].Value);
                if (call != null) results.Add(call);
            }

            if (results.Count > 0) return results;

            // 4) Inline JSON: {"name":"...", "arguments":{...}}
            var inlinePattern = new Regex(
                @"\{\s*""name""\s*:\s*""([a-z_]+)""\s*,\s*""(?:arguments|args|parameters)""\s*:\s*(\{(?:[^{}]|\{[^{}]*\})*\})\s*\}",
                RegexOptions.Singleline);

            foreach (Match match in inlinePattern.Matches(text))
            {
                var funcName = match.Groups[1].Value;
                if (!_allToolNames.Contains(funcName)) continue;

                var args = ParseArguments(match.Groups[2].Value);
                results.Add(new RevitChat.Models.ToolCallRequest
                {
                    ToolCallId = GenerateCallId(funcName),
                    FunctionName = funcName,
                    Arguments = args
                });
            }

            if (results.Count > 0) return results;

            // 5) Last resort: find any known tool name in garbled text (skip if text is short or lacks patterns)
            if (text.Length > 20 && (text.Contains('_') || text.Contains("arguments")))
            {
                foreach (var toolName in _allToolNames)
                {
                    if (!text.Contains(toolName, StringComparison.OrdinalIgnoreCase)) continue;
                    var pattern = new Regex(
                        $@"(?:^|[\s""':,])({Regex.Escape(toolName)})\s*[\(,:]?\s*\{{(.*?)\}}",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    var m = pattern.Match(text);
                    if (m.Success)
                    {
                        var argsText = "{" + m.Groups[2].Value + "}";
                        var call = TryParseOneToolCallWithName(toolName, argsText);
                        if (call != null) results.Add(call);
                    }
                }
            }

            if (results.Count > 0) return results;

            // 6) Fuzzy: extract quoted tool-like name + "arguments" block
            var fuzzyPattern = new Regex(
                @"""([a-z_]{3,})""\s*,\s*""arguments""\s*:\s*(\{(?:[^{}]|\{[^{}]*\})*\})",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var fm = fuzzyPattern.Match(text);
            if (fm.Success)
            {
                var candidateName = fm.Groups[1].Value;
                var resolved = _allToolNames.Contains(candidateName) ? candidateName : FuzzyMatchToolName(candidateName);
                if (resolved != null)
                {
                    var call = TryParseOneToolCallWithName(resolved, fm.Groups[2].Value);
                    if (call != null) results.Add(call);
                }
            }

            if (results.Count > 5)
                results = results.Take(2).ToList();

            return results;
        }

        private RevitChat.Models.ToolCallRequest TryParseOneToolCallWithName(string knownName, string argsJson)
        {
            try
            {
                var sanitized = SanitizeJsonLike(argsJson);
                using var doc = JsonDocument.Parse(sanitized);
                var args = new Dictionary<string, object>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    args[prop.Name] = prop.Value.Clone();

                return new RevitChat.Models.ToolCallRequest
                {
                    ToolCallId = GenerateCallId(knownName),
                    FunctionName = knownName,
                    Arguments = args
                };
            }
            catch
            {
                return new RevitChat.Models.ToolCallRequest
                {
                    ToolCallId = GenerateCallId(knownName),
                    FunctionName = knownName,
                    Arguments = new Dictionary<string, object>()
                };
            }
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
        }

        private string FuzzyMatchToolName(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return null;

            try
            {
                var corrected = RevitChat.Services.ChatFeedbackService.FindToolCorrection(candidate);
                if (corrected != null && _allToolNames.Contains(corrected))
                    return corrected;
            }
            catch { }

            var lower = candidate.ToLowerInvariant().Replace("-", "_");

            if (_allToolNames.Contains(lower)) return lower;

            foreach (var tool in _allToolNames)
            {
                if (tool.Contains(lower) || lower.Contains(tool))
                    return tool;
            }

            string best = null;
            int bestDist = int.MaxValue;
            foreach (var tool in _allToolNames)
            {
                var dist = LevenshteinDistance(lower, tool);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = tool;
                }
            }

            int maxLen = Math.Max(lower.Length, best?.Length ?? 0);
            if (maxLen > 0 && bestDist <= maxLen / 3)
                return best;

            var parts = lower.Split('_');
            best = null;
            int bestScore = 0;
            foreach (var tool in _allToolNames)
            {
                int score = 0;
                foreach (var part in parts)
                {
                    if (part.Length >= 3 && tool.Contains(part))
                        score += part.Length;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = tool;
                }
            }

            return bestScore >= 3 ? best : null;
        }

        private RevitChat.Models.ToolCallRequest TryParseOneToolCall(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string name = null;
                if (root.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString();

                if (string.IsNullOrEmpty(name)) return null;
                if (!_allToolNames.Contains(name))
                    name = FuzzyMatchToolName(name);
                if (name == null) return null;

                var args = new Dictionary<string, object>();

                if (TryReadArguments(root, out var parsedArgs))
                {
                    foreach (var kvp in parsedArgs)
                        args[kvp.Key] = kvp.Value;
                }
                else
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name != "name")
                            args[prop.Name] = prop.Value.Clone();
                    }
                }

                return new RevitChat.Models.ToolCallRequest
                {
                    ToolCallId = GenerateCallId(name),
                    FunctionName = name,
                    Arguments = args
                };
            }
            catch
            {
                try
                {
                    var cleaned = SanitizeJsonLike(json);
                    using var doc = JsonDocument.Parse(cleaned);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("name", out var nameProp)) return null;
                    var name = nameProp.GetString();
                    if (string.IsNullOrEmpty(name)) return null;
                    if (!_allToolNames.Contains(name))
                        name = FuzzyMatchToolName(name);
                    if (name == null) return null;

                    var args = new Dictionary<string, object>();
                    if (TryReadArguments(root, out var parsedArgs))
                    {
                        foreach (var kvp in parsedArgs)
                            args[kvp.Key] = kvp.Value;
                    }
                    else
                    {
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Name != "name")
                                args[prop.Name] = prop.Value.Clone();
                        }
                    }

                    return new RevitChat.Models.ToolCallRequest
                    {
                        ToolCallId = GenerateCallId(name),
                        FunctionName = name,
                        Arguments = args
                    };
                }
                catch
                {
                    return null;
                }
            }
        }

        private static bool TryReadArguments(JsonElement root, out Dictionary<string, object> args)
        {
            args = null;

            if (root.TryGetProperty("arguments", out var argsProp) ||
                root.TryGetProperty("args", out argsProp) ||
                root.TryGetProperty("parameters", out argsProp) ||
                root.TryGetProperty("elements", out argsProp))
            {
                if (argsProp.ValueKind == JsonValueKind.Object)
                {
                    args = new Dictionary<string, object>();
                    foreach (var prop in argsProp.EnumerateObject())
                        args[prop.Name] = prop.Value.Clone();
                    return true;
                }

                if (argsProp.ValueKind == JsonValueKind.String)
                {
                    var str = argsProp.GetString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(str);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                args = new Dictionary<string, object>();
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                    args[prop.Name] = prop.Value.Clone();
                                return true;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return false;
        }

        private static string SanitizeJsonLike(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            var cleaned = text.Trim();
            cleaned = Regex.Replace(cleaned, @"\s+,\s*}", "}", RegexOptions.Singleline);
            cleaned = Regex.Replace(cleaned, @"\s+,\s*]", "]", RegexOptions.Singleline);
            cleaned = Regex.Replace(cleaned, @"'(\w+)'\s*:", "\"$1\":");
            cleaned = Regex.Replace(cleaned, @":\s*'([^']*)'", ": \"$1\"");
            cleaned = Regex.Replace(cleaned, @"\bNone\b", "null");
            return cleaned;
        }

        private static bool LooksLikeToolCall(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("tool_call", StringComparison.OrdinalIgnoreCase)
                && text.Contains("{")) return true;
            if (Regex.IsMatch(text, @"""name""\s*:\s*""[a-z_]+""", RegexOptions.IgnoreCase)) return true;
            if (text.Contains("\"arguments\"") && text.Contains("{")) return true;
            if (Regex.IsMatch(text, @"[a-z_]{3,}\(.*\)", RegexOptions.IgnoreCase)
                && text.Contains("\"")) return true;
            if (Regex.IsMatch(text, @"""[a-z_]{3,}""\s*,\s*""arguments""", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        private const string FallbackMessage =
            "This matter haven't yet train, Please contact your Digital Lead to update me.";

        private string BuildFallbackSuggestion(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return FallbackMessage;

            var normalized = NormalizeForMatching(userMessage);
            var matches = GetMatchedGroups(normalized);

            if (matches.Count > 0)
            {
                var topGroup = matches.OrderByDescending(m => m.score).First().group;
                var toolSuggestions = string.Join(", ", topGroup.Tools.Take(3));
                var isVi = IsVietnamese(userMessage);
                return isVi
                    ? $"Mình chưa xử lý được yêu cầu này tự động. Bạn có thể thử cụ thể hơn? Ví dụ dùng lệnh: {toolSuggestions}"
                    : $"I couldn't process this automatically. Try being more specific. Related tools: {toolSuggestions}";
            }

            return FallbackMessage;
        }

        public List<string> ValidateToolCalls(List<RevitChat.Models.ToolCallRequest> toolCalls)
        {
            var errors = new List<string>();
            if (toolCalls == null) return errors;

            foreach (var call in toolCalls)
            {
                if (!ToolSchemaHints.TryGetValue(call.FunctionName, out var hint))
                    continue;
                if (hint == "no args") continue;

                var requiredParams = new List<string>();
                foreach (var part in hint.Split(','))
                {
                    var p = part.Trim();
                    if (string.IsNullOrEmpty(p)) continue;
                    var paramName = p.Split(' ')[0].TrimEnd('?');
                    if (!p.Contains('?'))
                        requiredParams.Add(paramName);
                }

                foreach (var req in requiredParams)
                {
                    if (call.Arguments == null || !call.Arguments.ContainsKey(req))
                        errors.Add($"Tool '{call.FunctionName}' missing required parameter: {req}");
                }
            }
            return errors;
        }

        public async Task<(string, List<RevitChat.Models.ToolCallRequest>)> RetryWithValidationErrorAsync(
            List<string> errors, CancellationToken ct)
        {
            var errorMsg = string.Join("\n", errors);
            _conversationHistory.Add(new UserChatMessage(
                $"Your tool call had errors:\n{errorMsg}\nPlease fix and try again with correct parameters."));
            return await GetCompletionWithRetryAsync(ct);
        }

        private string RemoveToolCallTags(string text)
        {
            text = Regex.Replace(text, @"<tool_call>.*?</tool_call>", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"<tool_call>.*$", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"^.*?</tool_call>", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"</?tool_call>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"```json?\s*\{[^`]*?""name""[^`]*?\}\s*```", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"```json?\s*<tool_call>.*?</tool_call>\s*```", "", RegexOptions.Singleline);
            return text.Trim();
        }

        private string BuildSystemPrompt(List<string> forcedTools = null)
        {
            var userMsg = _conversationHistory.Count > 0 ? _lastUserMessage : "";

            if (forcedTools == null && _cachedSystemPrompt != null)
            {
                var currentKeywords = NormalizeForMatching(userMsg);
                if (currentKeywords == _cachedPromptKeywords)
                    return _cachedSystemPrompt;
            }

            var catalog = BuildToolCatalogForMessage(userMsg, forcedTools);
            var dynamicExamples = BuildDynamicExamples(userMsg);

            var tagKnowledgeSection = "";
            if (!string.IsNullOrEmpty(SmartTagKnowledgeSummary))
            {
                var lowerMsg = (userMsg ?? "").ToLowerInvariant();
                if (lowerMsg.Contains("tag") || lowerMsg.Contains("ghi chú") || lowerMsg.Contains("annotation")
                    || lowerMsg.Contains("chú thích") || lowerMsg.Contains("untagged") || lowerMsg.Contains("chưa tag"))
                {
                    tagKnowledgeSection = $"\n\n{SmartTagKnowledgeSummary}\n";
                }
            }

            var examplesSection = !string.IsNullOrEmpty(dynamicExamples)
                ? $"## EXAMPLES (follow these patterns)\n\n{dynamicExamples}"
                : @"## EXAMPLES

User: How many walls are in the model?
Assistant:
<tool_call>
{""name"": ""count_elements"", ""arguments"": {""category"": ""Walls""}}
</tool_call>

User: đổi màu tất cả duct sang đỏ
Assistant:
<tool_call>
{""name"": ""override_category_color"", ""arguments"": {""category"": ""Ducts"", ""color"": ""Red""}}
</tool_call>";

            var prompt = $@"You are a Revit BIM assistant. Execute tools to answer queries. Fully understand English, Vietnamese, and mixed-language prompts (e.g. 'isolate tất cả duct trên Level 1'). Handle missing diacritics (e.g. 'ong gio' = 'ống gió' = Ducts).

## TOOLS
{catalog}

## FORMAT
<tool_call>
{{""name"": ""tool_name"", ""arguments"": {{""param1"": ""value1""}}}}
</tool_call>

## RULES
1. Output <tool_call> immediately for data queries. No explanation before tool calls.
2. ONE <tool_call> per response. For independent queries (count pipes AND ducts), up to 3.
3. No code fences around <tool_call>. ""arguments"" must be a JSON object.
4. After tool results, answer directly. Need more data → one more <tool_call>.
5. Multi-step: handle FIRST step only. Next step after results.
6. Destructive ops (delete/modify): confirm first.
7. NEVER invent data. Reply in the language the user primarily uses.
8. Vietnamese domain terms: tường=Walls, cửa=Doors, cửa sổ=Windows, ống gió=Ducts, ống nước=Pipes, ống dẫn=Conduits, phòng=Rooms, sàn=Floors, cột=Columns, dầm=Structural Framing, trần=Ceilings, mái=Roofs, cầu thang=Stairs, thiết bị vệ sinh=Plumbing Fixtures, khay cáp=Cable Trays, đèn=Lighting Fixtures, van=Valves, quạt=Fans, bơm=Pumps, phụ kiện=Fittings, bảo ôn=Insulation, cô lập=Isolate, ẩn=Hide, hiện=Unhide, tô màu=Override Color, xuất=Export, kiểm tra=Check/Audit, kết cấu=Structural, liên kết=Link, mạch=Circuit, bảng điện=Electrical Panel.
9. No matching tool → reply: ""This matter haven't yet train, Please contact your Digital Lead to update me.""
10. Prefer specific tools (get_duct_summary > get_elements). If 'selected'/'chọn' → use [Context] IDs or get_current_selection.
11. MEP queries → prefer get_pipe_summary, get_duct_summary over get_elements.

{examplesSection}
{tagKnowledgeSection}";

            var ctx = RevitChat.Services.PromptAnalyzer.Analyze(userMsg);
            if (!string.IsNullOrEmpty(ctx.ContextHint))
                prompt += $"\n\n{ctx.ContextHint}";

            // Phase 3: Project context profile
            try
            {
                var projectProfile = RevitChat.Services.ProjectContextMemory.GetProfileSummary();
                if (!string.IsNullOrEmpty(projectProfile))
                    prompt += $"\n\n{projectProfile}";
            }
            catch { }

            if (forcedTools == null)
            {
                _cachedSystemPrompt = prompt;
                _cachedPromptKeywords = NormalizeForMatching(userMsg);
            }
            return prompt;
        }

        private List<OaiMessage> BuildMessages(List<string> forcedTools = null)
        {
            var messages = new List<OaiMessage>
            {
                new SystemChatMessage(BuildSystemPrompt(forcedTools))
            };
            messages.AddRange(_conversationHistory);
            return messages;
        }

        private static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Length / 4;
        }

        private static int GetMessageTokens(OaiMessage msg)
        {
            if (msg is UserChatMessage um)
                return EstimateTokens(um.Content?.FirstOrDefault()?.Text);
            if (msg is AssistantChatMessage am)
                return EstimateTokens(am.Content?.FirstOrDefault()?.Text);
            if (msg is SystemChatMessage sm)
                return EstimateTokens(sm.Content?.FirstOrDefault()?.Text);
            return 50;
        }

        private void TrimHistory()
        {
            var config = LocalConfigService.Load();
            int max = config.MaxConversationMessages;
            const int maxTokenBudget = 12000;
            const int keepRecentCount = 6;

            int totalTokens = _conversationHistory.Sum(GetMessageTokens);
            while (totalTokens > maxTokenBudget && _conversationHistory.Count > keepRecentCount)
            {
                totalTokens -= GetMessageTokens(_conversationHistory[0]);
                _conversationHistory.RemoveAt(0);
                while (_conversationHistory.Count > keepRecentCount && _conversationHistory[0] is ToolChatMessage)
                {
                    totalTokens -= GetMessageTokens(_conversationHistory[0]);
                    _conversationHistory.RemoveAt(0);
                }
            }

            if (_conversationHistory.Count > max)
            {
                int toSummarize = _conversationHistory.Count - (max / 2);
                if (toSummarize > 2)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("[Conversation summary of earlier messages]");
                    for (int i = 0; i < toSummarize; i++)
                    {
                        var msg = _conversationHistory[i];
                        if (msg is UserChatMessage um)
                        {
                            var text = um.Content?.FirstOrDefault()?.Text ?? "";
                            if (text.Length > 100) text = text[..100] + "...";
                            sb.AppendLine($"- User: {text}");
                        }
                        else if (msg is AssistantChatMessage am)
                        {
                            var text = am.Content?.FirstOrDefault()?.Text ?? "";
                            if (text.Contains("tool call") || text.Contains("<tool_call>"))
                                sb.AppendLine($"- Assistant: executed tool");
                            else if (text.Length > 120)
                                sb.AppendLine($"- Assistant: {text[..120]}...");
                            else
                                sb.AppendLine($"- Assistant: {text}");
                        }
                    }

                    _conversationHistory.RemoveRange(0, toSummarize);
                    _conversationHistory.Insert(0, new SystemChatMessage(sb.ToString()));
                }
            }

            while (_conversationHistory.Count > max)
                _conversationHistory.RemoveAt(0);
        }

        private static string GenerateCallId(string funcName)
        {
            return $"call_{funcName}_{Guid.NewGuid():N}"[..32];
        }

        private static Dictionary<string, object> ParseArguments(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, object>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, object>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.Clone();
                return dict;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }
    }
}
