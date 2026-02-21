using System;
using System.ClientModel;
using System.Collections.Generic;
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
    /// <summary>
    /// Ollama chat service using prompt-based tool calling.
    /// Tools are described in the system prompt; the model outputs
    /// &lt;tool_call&gt; tags which are parsed and executed.
    /// </summary>
    public class OllamaChatService : RevitChat.Models.IChatService
    {
        private ChatClient _client;
        private readonly List<OaiMessage> _conversationHistory = new();
        private readonly SkillRegistry _skillRegistry;
        private string _toolCatalog;
        private HashSet<string> _allToolNames;

        public OllamaChatService(SkillRegistry skillRegistry)
        {
            _skillRegistry = skillRegistry;
            BuildToolCatalog();
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
                throw new InvalidOperationException("Ollama client not initialized.");

            _conversationHistory.Add(new UserChatMessage(userMessage));
            TrimHistory();

            return await GetCompletionAsync(ct);
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tool execution results:");
            foreach (var kvp in toolResults)
            {
                sb.AppendLine($"[{kvp.Key}] Result: {kvp.Value}");
            }
            sb.AppendLine();
            sb.AppendLine("Now analyze the results above and provide a clear, helpful answer to the user. " +
                          "If you need more data, make another <tool_call>. Otherwise, respond directly.");

            _conversationHistory.Add(new UserChatMessage(sb.ToString()));

            return await GetCompletionAsync(ct);
        }

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionAsync(
            CancellationToken ct)
        {
            var config = LocalConfigService.Load();

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = config.MaxTokens,
            };

            var messages = BuildMessages();

            var response = await _client.CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            var text = completion.Content?.FirstOrDefault()?.Text ?? "";

            var toolCalls = ExtractToolCalls(text);
            if (toolCalls.Count > 0)
            {
                var cleanText = RemoveToolCallTags(text).Trim();
                if (!string.IsNullOrEmpty(cleanText))
                    _conversationHistory.Add(new AssistantChatMessage(cleanText));
                else
                    _conversationHistory.Add(new AssistantChatMessage(
                        $"[Executing {toolCalls.Count} tool(s)...]"));

                return (null, toolCalls);
            }

            _conversationHistory.Add(new AssistantChatMessage(text));
            return (text, new List<RevitChat.Models.ToolCallRequest>());
        }

        private List<RevitChat.Models.ToolCallRequest> ExtractToolCalls(string text)
        {
            var results = new List<RevitChat.Models.ToolCallRequest>();
            if (string.IsNullOrWhiteSpace(text)) return results;

            var tagPattern = new Regex(
                @"<tool_call>\s*(\{.*?\})\s*</tool_call>",
                RegexOptions.Singleline);

            foreach (Match match in tagPattern.Matches(text))
            {
                var call = TryParseOneToolCall(match.Groups[1].Value);
                if (call != null) results.Add(call);
            }

            if (results.Count > 0) return results;

            var jsonBlockPattern = new Regex(
                @"```(?:json)?\s*(\{[^`]*?""name""\s*:\s*""[a-z_]+""\s*[^`]*?\})\s*```",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match match in jsonBlockPattern.Matches(text))
            {
                var call = TryParseOneToolCall(match.Groups[1].Value);
                if (call != null) results.Add(call);
            }

            if (results.Count > 0) return results;

            var inlinePattern = new Regex(
                @"\{\s*""name""\s*:\s*""([a-z_]+)""\s*,\s*""arguments""\s*:\s*(\{(?:[^{}]|\{[^{}]*\})*\})\s*\}",
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

            if (results.Count > 5)
                results = results.Take(1).ToList();

            return results;
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

                if (string.IsNullOrEmpty(name) || !_allToolNames.Contains(name))
                    return null;

                var args = new Dictionary<string, object>();
                if (root.TryGetProperty("arguments", out var argsProp) &&
                    argsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsProp.EnumerateObject())
                        args[prop.Name] = prop.Value.Clone();
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

        private string RemoveToolCallTags(string text)
        {
            text = Regex.Replace(text, @"<tool_call>.*?</tool_call>", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"```json?\s*\{[^`]*?""name""[^`]*?\}\s*```", "", RegexOptions.Singleline);
            return text.Trim();
        }

        private void BuildToolCatalog()
        {
            _allToolNames = new HashSet<string>();
            var sb = new StringBuilder();

            var config = LocalConfigService.Load();
            int maxTools = config.MaxTools;
            var allTools = _skillRegistry.GetAllToolDefinitions();
            var tools = maxTools > 0 && allTools.Count > maxTools
                ? allTools.Take(maxTools).ToList()
                : allTools.ToList();

            foreach (var tool in allTools)
                _allToolNames.Add(tool.FunctionName);

            foreach (var tool in tools)
                sb.AppendLine($"- {tool.FunctionName}: {tool.FunctionDescription}");

            _toolCatalog = sb.ToString();
        }

        private string BuildSystemPrompt()
        {
            return $@"You are a Revit BIM assistant embedded inside Autodesk Revit.
You help users query, analyze, modify, and export building model data.

## AVAILABLE TOOLS
{_toolCatalog}

## HOW TO CALL TOOLS
When you need data from the Revit model, you MUST output a tool call using this EXACT format:

<tool_call>
{{""name"": ""tool_name"", ""arguments"": {{""param1"": ""value1"", ""param2"": 123}}}}
</tool_call>

## CRITICAL RULES
1. ALWAYS call a tool when the user asks about model data. NEVER make up data.
2. Output the <tool_call> tag - do NOT just describe which tool to use.
3. Only ONE tool call per response. Wait for the result before calling another.
4. For destructive operations (delete, modify), ask the user to confirm FIRST.
5. After receiving tool results, present them clearly. Use tables for lists.
6. Reply in the same language the user uses (Vietnamese or English).
7. Keep answers concise but complete.

## EXAMPLES

User: How many walls are in the model?
Assistant: Let me check the wall count.
<tool_call>
{{""name"": ""get_element_count"", ""arguments"": {{""category"": ""Walls""}}}}
</tool_call>

User: Show me all levels
Assistant: I'll get the level information for you.
<tool_call>
{{""name"": ""get_levels"", ""arguments"": {{}}}}
</tool_call>

User: What categories are available?
Assistant: Let me retrieve the categories.
<tool_call>
{{""name"": ""get_categories"", ""arguments"": {{}}}}
</tool_call>";
        }

        private List<OaiMessage> BuildMessages()
        {
            var messages = new List<OaiMessage>
            {
                new SystemChatMessage(BuildSystemPrompt())
            };
            messages.AddRange(_conversationHistory);
            return messages;
        }

        private void TrimHistory()
        {
            var config = LocalConfigService.Load();
            int max = config.MaxConversationMessages;
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
