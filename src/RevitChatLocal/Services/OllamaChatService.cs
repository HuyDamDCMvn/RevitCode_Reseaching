using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
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
    public class OllamaChatService
    {
        private ChatClient _client;
        private readonly List<OaiMessage> _conversationHistory = new();
        private readonly SkillRegistry _skillRegistry;

        private const string SystemPrompt = @"You are a Revit BIM assistant embedded inside Autodesk Revit.
You help users query, analyze, modify, and export building model data using the tools provided.

CRITICAL RULES FOR TOOL CALLING:
- You MUST use the provided function tools to get data. NEVER guess or fabricate data.
- When the user asks about model data, CALL the appropriate tool function - do NOT describe or explain what tool to use.
- Do NOT output JSON manually. Use the function calling mechanism provided.
- For destructive operations (delete, modify), confirm with user FIRST.
- Present results in clear format. Use tables for lists.
- Reply in the same language the user uses (Vietnamese or English).
- If a tool returns an error, explain it clearly and suggest alternatives.
- Keep answers concise but complete.";

        public OllamaChatService(SkillRegistry skillRegistry)
        {
            _skillRegistry = skillRegistry;
        }

        public void Initialize(string endpointUrl, string model)
        {
            var endpoint = endpointUrl.TrimEnd('/');
            if (!endpoint.EndsWith("/v1"))
                endpoint += "/v1";
            endpoint += "/";

            var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
            var client = new OpenAIClient(new ApiKeyCredential("ollama"), options);
            _client = client.GetChatClient(model);
        }

        public bool IsInitialized => _client != null;

        public void ClearHistory()
        {
            _conversationHistory.Clear();
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default)
        {
            if (_client == null)
                throw new InvalidOperationException("Ollama client not initialized. Please check your endpoint settings.");

            _conversationHistory.Add(new UserChatMessage(userMessage));
            TrimHistory();

            return await GetCompletionAsync(ct);
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            foreach (var kvp in toolResults)
            {
                _conversationHistory.Add(new ToolChatMessage(kvp.Key, kvp.Value));
            }

            return await GetCompletionAsync(ct);
        }

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionAsync(
            CancellationToken ct)
        {
            var config = LocalConfigService.Load();
            var allTools = _skillRegistry.GetAllToolDefinitions();

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = config.MaxTokens,
            };

            int maxTools = config.MaxTools;
            var toolsToSend = maxTools > 0 && allTools.Count > maxTools
                ? SelectRelevantTools(allTools, maxTools)
                : allTools;

            foreach (var tool in toolsToSend)
                options.Tools.Add(tool);

            var messages = BuildMessages();

            var response = await _client.CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls &&
                completion.ToolCalls != null && completion.ToolCalls.Count > 0)
            {
                var assistantMsg = new AssistantChatMessage(completion);
                _conversationHistory.Add(assistantMsg);

                var toolCalls = new List<RevitChat.Models.ToolCallRequest>();
                foreach (var tc in completion.ToolCalls)
                {
                    var args = ParseArguments(tc.FunctionArguments?.ToString());
                    toolCalls.Add(new RevitChat.Models.ToolCallRequest
                    {
                        ToolCallId = tc.Id,
                        FunctionName = tc.FunctionName,
                        Arguments = args
                    });
                }

                return (null, toolCalls);
            }

            var text = completion.Content?.FirstOrDefault()?.Text ?? "";

            var fallbackCalls = TryParseToolCallsFromText(text);
            if (fallbackCalls.Count > 0)
            {
                _conversationHistory.Add(new AssistantChatMessage(
                    $"[Calling {fallbackCalls.Count} tool(s)...]"));

                return (null, fallbackCalls);
            }

            _conversationHistory.Add(new AssistantChatMessage(text));
            return (text, new List<RevitChat.Models.ToolCallRequest>());
        }

        /// <summary>
        /// Fallback: parse tool calls from text when Ollama doesn't return structured tool_calls.
        /// Detects patterns like {"name": "tool_name", "arguments": {...}}
        /// </summary>
        private List<RevitChat.Models.ToolCallRequest> TryParseToolCallsFromText(string text)
        {
            var results = new List<RevitChat.Models.ToolCallRequest>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            var allToolNames = new HashSet<string>();
            foreach (var skill in _skillRegistry.Skills)
            {
                foreach (var tool in skill.GetToolDefinitions())
                    allToolNames.Add(tool.FunctionName);
            }

            var jsonPattern = new Regex(@"\{[^{}]*""name""\s*:\s*""([^""]+)""[^{}]*""arguments""\s*:\s*(\{[^{}]*\})[^{}]*\}",
                RegexOptions.Singleline);

            foreach (Match match in jsonPattern.Matches(text))
            {
                var funcName = match.Groups[1].Value;
                var argsJson = match.Groups[2].Value;

                if (!allToolNames.Contains(funcName)) continue;

                var args = ParseArguments(argsJson);
                results.Add(new RevitChat.Models.ToolCallRequest
                {
                    ToolCallId = $"fallback_{funcName}_{Guid.NewGuid():N}".Substring(0, 32),
                    FunctionName = funcName,
                    Arguments = args
                });
            }

            if (results.Count > 0) return results;

            var simplePattern = new Regex(@"""?name""?\s*:\s*""([a-z_]+)""", RegexOptions.IgnoreCase);
            foreach (Match match in simplePattern.Matches(text))
            {
                var funcName = match.Groups[1].Value;
                if (!allToolNames.Contains(funcName)) continue;

                var argsMatch = Regex.Match(text.Substring(match.Index),
                    @"""arguments""\s*:\s*(\{[^{}]*\})", RegexOptions.Singleline);

                var args = argsMatch.Success ? ParseArguments(argsMatch.Groups[1].Value) : new Dictionary<string, object>();

                if (!results.Any(r => r.FunctionName == funcName))
                {
                    results.Add(new RevitChat.Models.ToolCallRequest
                    {
                        ToolCallId = $"fallback_{funcName}_{Guid.NewGuid():N}".Substring(0, 32),
                        FunctionName = funcName,
                        Arguments = args
                    });
                }
            }

            if (results.Count > 3)
                results = results.Take(1).ToList();

            return results;
        }

        /// <summary>
        /// Select a subset of tools most likely relevant to the current conversation.
        /// Always includes core query tools; adds others based on recent messages.
        /// </summary>
        private IReadOnlyList<ChatTool> SelectRelevantTools(IReadOnlyList<ChatTool> allTools, int maxTools)
        {
            var coreTools = new HashSet<string>
            {
                "get_elements", "get_element_count", "get_element_parameters",
                "get_categories", "search_elements",
                "get_project_info", "get_levels", "get_views",
                "modify_parameter", "delete_elements",
                "get_model_statistics", "get_model_warnings"
            };

            var selected = new List<ChatTool>();

            foreach (var tool in allTools)
            {
                if (coreTools.Contains(tool.FunctionName))
                    selected.Add(tool);
            }

            var recentText = GetRecentConversationText().ToLower();

            var keywordMap = new Dictionary<string, string[]>
            {
                { "mep", new[] { "mep", "duct", "pipe", "hvac", "mechanical", "electrical", "plumbing", "cable", "conduit", "fitting" } },
                { "sheet", new[] { "sheet", "viewport", "titleblock", "print" } },
                { "family", new[] { "family", "place", "swap", "load", "type", "symbol" } },
                { "clash", new[] { "clash", "intersect", "collision", "clearance", "overlap" } },
                { "material", new[] { "material", "concrete", "steel", "glass", "wood" } },
                { "room", new[] { "room", "area", "space", "finish", "boundary" } },
                { "filter", new[] { "filter", "template", "view template", "visibility" } },
                { "tag", new[] { "tag", "annotate", "dimension", "text note", "label" } },
                { "workset", new[] { "workset", "phase", "demolish" } },
                { "group", new[] { "group", "ungroup" } },
                { "link", new[] { "link", "linked", "xref" } },
                { "revision", new[] { "revision", "cloud", "markup", "issue" } },
                { "grid", new[] { "grid", "level", "axis", "alignment" } },
                { "warning", new[] { "warning", "health", "audit", "purge", "unused", "duplicate", "cad", "import" } },
                { "naming", new[] { "naming", "convention", "standard", "audit name" } },
                { "parameter", new[] { "shared parameter", "project parameter", "binding", "add parameter" } },
                { "select", new[] { "select", "highlight", "pick", "selection" } },
                { "coordination", new[] { "coordination", "scope box", "compare", "report" } },
                { "export", new[] { "export", "csv", "schedule", "boq" } },
                { "hide", new[] { "hide", "unhide", "isolate", "visible", "visibility" } },
                { "copy", new[] { "copy", "move", "mirror", "duplicate", "rename" } },
            };

            var relevantSkillPrefixes = new HashSet<string>();
            foreach (var (key, keywords) in keywordMap)
            {
                if (keywords.Any(k => recentText.Contains(k)))
                    relevantSkillPrefixes.Add(key);
            }

            foreach (var tool in allTools)
            {
                if (selected.Count >= maxTools) break;
                if (selected.Any(t => t.FunctionName == tool.FunctionName)) continue;

                var fn = tool.FunctionName.ToLower();
                bool isRelevant = relevantSkillPrefixes.Any(prefix =>
                    fn.Contains(prefix) || MatchesKeywordGroup(fn, prefix, keywordMap));

                if (isRelevant)
                    selected.Add(tool);
            }

            foreach (var tool in allTools)
            {
                if (selected.Count >= maxTools) break;
                if (!selected.Any(t => t.FunctionName == tool.FunctionName))
                    selected.Add(tool);
            }

            return selected;
        }

        private bool MatchesKeywordGroup(string funcName, string group, Dictionary<string, string[]> map)
        {
            if (!map.ContainsKey(group)) return false;
            return map[group].Any(k => funcName.Contains(k));
        }

        private string GetRecentConversationText()
        {
            var parts = new List<string>();
            var recent = _conversationHistory.TakeLast(6);
            foreach (var msg in recent)
            {
                if (msg is UserChatMessage ucm)
                {
                    foreach (var part in ucm.Content)
                        parts.Add(part.Text ?? "");
                }
            }
            return string.Join(" ", parts);
        }

        private List<OaiMessage> BuildMessages()
        {
            var messages = new List<OaiMessage>
            {
                new SystemChatMessage(SystemPrompt)
            };
            messages.AddRange(_conversationHistory);
            return messages;
        }

        private void TrimHistory()
        {
            var config = LocalConfigService.Load();
            int max = config.MaxConversationMessages;
            while (_conversationHistory.Count > max)
            {
                _conversationHistory.RemoveAt(0);
            }
        }

        private static Dictionary<string, object> ParseArguments(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, object>();
            try
            {
                var doc = JsonDocument.Parse(json);
                var dict = new Dictionary<string, object>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value;
                }
                return dict;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }
    }
}
