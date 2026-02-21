using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using RevitChat.Skills;

using OaiMessage = OpenAI.Chat.ChatMessage;

namespace RevitChat.Services
{
    /// <summary>
    /// Manages OpenAI Chat Completions with function calling.
    /// Tools are sourced from the SkillRegistry, making capabilities modular.
    /// Runs entirely on background threads -- never touches Revit API.
    /// </summary>
    public class OpenAiChatService : Models.IChatService
    {
        private ChatClient _client;
        private readonly List<OaiMessage> _conversationHistory = new();
        private readonly SkillRegistry _skillRegistry;

        private const string SystemPrompt = @"You are a Revit BIM assistant embedded inside Autodesk Revit.
You help users query, analyze, modify, and export building model data using the tools provided.

Rules:
- ALWAYS use the provided tools to get data from the Revit model. Never guess or fabricate data.
- For destructive operations (delete, modify parameters), ALWAYS confirm with the user first by listing what will change. Only proceed after the user explicitly agrees.
- When querying elements, use get_categories or get_levels first if the user's question is ambiguous.
- Present data in clear, structured format. Use tables for lists.
- Support both Vietnamese and English — reply in the same language the user uses.
- When exporting, suggest a Desktop path if the user doesn't specify one.
- For large datasets (>50 elements), summarize first and offer to export to CSV.
- If a tool returns an error, explain it clearly and suggest alternatives.
- Keep answers concise but complete.";

        public OpenAiChatService(SkillRegistry skillRegistry)
        {
            _skillRegistry = skillRegistry;
        }

        public void Initialize(string apiKey, string model)
        {
            var client = new OpenAIClient(apiKey);
            _client = client.GetChatClient(model);
        }

        public bool IsInitialized => _client != null;

        public void ClearHistory()
        {
            _conversationHistory.Clear();
        }

        public async Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default)
        {
            if (_client == null)
                throw new InvalidOperationException("OpenAI client not initialized. Please set your API key.");

            _conversationHistory.Add(new UserChatMessage(userMessage));
            TrimHistory();

            return await GetCompletionAsync(ct);
        }

        public async Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            foreach (var kvp in toolResults)
            {
                _conversationHistory.Add(new ToolChatMessage(kvp.Key, kvp.Value));
            }

            return await GetCompletionAsync(ct);
        }

        private async Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> GetCompletionAsync(
            CancellationToken ct)
        {
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = ConfigService.Load().MaxTokens,
            };

            foreach (var tool in _skillRegistry.GetAllToolDefinitions())
                options.Tools.Add(tool);

            var messages = BuildMessages();

            var response = await _client.CompleteChatAsync(messages, options, ct);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                var assistantMsg = new AssistantChatMessage(completion);
                _conversationHistory.Add(assistantMsg);

                var toolCalls = new List<Models.ToolCallRequest>();
                foreach (var tc in completion.ToolCalls)
                {
                    var args = ParseArguments(tc.FunctionArguments?.ToString());
                    toolCalls.Add(new Models.ToolCallRequest
                    {
                        ToolCallId = tc.Id,
                        FunctionName = tc.FunctionName,
                        Arguments = args
                    });
                }

                return (null, toolCalls);
            }

            var text = completion.Content?.FirstOrDefault()?.Text ?? "";
            _conversationHistory.Add(new AssistantChatMessage(text));

            return (text, new List<Models.ToolCallRequest>());
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
            var config = ConfigService.Load();
            int max = config.MaxConversationMessages;
            while (_conversationHistory.Count > max)
            {
                _conversationHistory.RemoveAt(0);
                // Don't leave orphan ToolChatMessages at the start
                while (_conversationHistory.Count > 0 && _conversationHistory[0] is ToolChatMessage)
                    _conversationHistory.RemoveAt(0);
            }
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
