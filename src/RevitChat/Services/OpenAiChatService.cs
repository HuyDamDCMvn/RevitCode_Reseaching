using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
    /// Manages OpenAI Chat Completions with streaming and smart tool selection.
    /// Tools are sourced from the SkillRegistry, making capabilities modular.
    /// Runs entirely on background threads -- never touches Revit API.
    /// </summary>
    public class OpenAiChatService : Models.IChatService
    {
        private ChatClient _client;
        private readonly List<OaiMessage> _conversationHistory = new();
        private readonly SkillRegistry _skillRegistry;
        private string _lastUserMessage = "";
        private PromptContext _lastPromptContext;

        public event Action<string> DebugMessage;
        public event Action<string> TokenReceived;

        private const string SystemPrompt = @"You are a Revit BIM assistant embedded inside Autodesk Revit.
You help users query, analyze, modify, and export building model data using the tools provided.

Rules:
- ALWAYS use the provided tools to get data from the Revit model. Never guess or fabricate data.
- For destructive operations (delete, modify parameters), ALWAYS confirm with the user first by listing what will change. Only proceed after the user explicitly agrees.
- When querying elements, use get_categories or get_levels first if the user's question is ambiguous.
- Present data in clear, structured format. Use tables for lists.
- Fully support Vietnamese, English, and mixed-language prompts (e.g. 'isolate tất cả duct trên Level 1'). Reply in the language the user primarily uses. Understand intent regardless of language mixing or missing diacritics.
- Vietnamese domain terms: tường=Walls, cửa=Doors, cửa sổ=Windows, ống gió=Ducts, ống nước=Pipes, ống dẫn=Conduits, phòng=Rooms, sàn=Floors, cột=Columns, dầm=Structural Framing, trần=Ceilings, cầu thang=Stairs, khay cáp=Cable Trays, đèn=Lighting Fixtures, thiết bị vệ sinh=Plumbing Fixtures, van=Valves, quạt=Fans, bơm=Pumps, phụ kiện=Fittings, bảo ôn=Insulation, cô lập=Isolate, ẩn=Hide, hiện=Unhide, tô màu=Override Color, xuất=Export, kiểm tra=Check/Audit.
- When exporting, suggest a Desktop path if the user doesn't specify one.
- For large datasets (>50 elements), summarize first and offer to export to CSV.
- If a tool returns an error, explain it clearly and suggest alternatives.
- Keep answers concise but complete.";

        #region Keyword → Pack mapping for smart tool selection

        private static readonly (string[] Keywords, string[] Packs)[] KeywordPackMap = new[]
        {
            (new[] { "duct", "pipe", "mep", "ống", "hvac", "air", "gió", "nước", "flow", "lưu lượng",
                     "pressure", "áp suất", "insulation", "bảo ôn", "velocity", "vận tốc", "noise",
                     "fitting", "elbow", "tee", "reducer", "coupling", "tap", "opening",
                     "space", "zone", "airflow", "không gian",
                     "tuyến", "route", "routing", "riser", "đường ống", "connector", "kết nối",
                     "system", "hệ thống", "network", "traverse", "boq", "khối lượng",
                     "hanger", "giá đỡ", "size", "kích cỡ",
                     "ống gió", "ống nước", "cơ điện", "phụ kiện", "thoát nước", "cấp nước",
                     "thông gió", "điều hòa", "sprinkler", "pccc", "phòng cháy", "chữa cháy",
                     "bơm", "quạt", "van", "damper", "chiller", "boiler", "ống dẫn", "khay cáp",
                     "co nối", "đấu nối", "nối ống", "cảm biến", "sensor",
                     "báo giá", "dự toán", "bảng khối lượng", "thống kê", "tổng hợp",
                     "supply air", "return air", "exhaust", "outside air", "mixed air",
                     "cấp gió", "hồi gió", "hút gió", "gió tươi", "gió ngoài", "gió hòa",
                     "fresh air", "nước thải", "nước lạnh", "nước nóng", "nước mưa", "nước cấp",
                     "hot water", "chilled water", "cold water", "domestic water", "rain water", "waste water",
                     "chiều dài", "diện tích", "bề mặt", "length", "area", "surface",
                     "summary", "tóm tắt", "phân loại", "classify",
                     "AHU", "FCU", "VAV", "RTU", "VRF", "VRV", "ERV", "MAU", "PAU", "DOAS",
                     "SA", "RA", "EA", "OA", "FA", "HW", "CHW", "CW", "HWS", "HWR", "CHWS", "CHWR",
                     "FP", "FPS", "FHC", "FDC", "SW", "SV", "SD",
                     "MDB", "SMDB", "DB", "MCC", "UPS", "ATS",
                     "diffuser", "grille", "louver", "air terminal", "miệng gió",
                     "máy lạnh", "nồi hơi", "tháp giải nhiệt", "cooling tower",
                     "bình giãn nở", "bình tích áp", "expansion tank", "pressure tank",
                     "đầu phun", "ổ cắm", "công tắc", "tủ điện", "máng cáp",
                     "thiết bị cơ", "thiết bị điện", "thiết bị vệ sinh",
                     "PRV", "TMV", "valve", "pressure reducing",
                     "sanitary", "drainage", "storm", "vent" },
             new[] { "MEP" }),
            (new[] { "sheet", "schedule", "bản vẽ", "bảng", "dimension", "kích thước", "tag", "ghi chú",
                     "annotation", "revision", "phát hành", "family", "load family", "place",
                     "floor", "sàn", "room", "phòng", "area", "diện tích", "grid", "lưới",
                     "level", "tầng", "cao độ", "group", "nhóm", "material", "vật liệu",
                     "workset", "phase", "giai đoạn", "shared parameter",
                     "chú thích", "bố trí", "họ", "loại" },
             new[] { "Modeler" }),
            (new[] { "clash", "coordinate", "kiểm tra", "xung đột", "va chạm", "audit", "health",
                     "naming", "đặt tên", "purge", "dọn", "qc", "quality", "chất lượng",
                     "report", "báo cáo", "standard", "tiêu chuẩn", "duplicate", "trùng",
                     "sức khỏe", "cảnh báo", "warning", "dọn dẹp", "trùng lặp",
                     "phối hợp", "đánh giá" },
             new[] { "BIMCoordinator" }),
            (new[] { "view", "filter", "template", "override", "color", "màu", "isolate", "cách ly",
                     "crop", "zoom", "callout", "drafting", "section", "mặt cắt", "3d", "screenshot",
                     "chọn", "selected", "selection", "đang chọn", "highlight",
                     "ẩn", "hiện", "phóng", "cô lập", "hiển thị", "hide", "unhide",
                     "nhìn", "tầm nhìn" },
             new[] { "ViewControl" }),
            (new[] { "link", "liên kết", "linked model", "xref", "mô hình liên kết" },
             new[] { "LinkedModels" }),
            (new[] { "electrical", "điện", "panel", "circuit", "voltage", "load", "phase balance",
                     "bảng điện", "mạch", "dây", "ổ cắm", "công tắc", "tủ điện" },
             new[] { "Electrical" }),
            (new[] { "structural", "kết cấu", "rebar", "cốt thép", "beam", "dầm", "column", "cột",
                     "foundation", "móng", "thép", "bê tông", "vách" },
             new[] { "Structure" }),
            (new[] { "energy", "năng lượng", "gbxml", "thermal", "nhiệt", "solar", "insulation",
                     "cách nhiệt", "tiết kiệm năng lượng" },
             new[] { "Energy" }),
            (new[] { "addin", "add-in", "plugin", "extension", "tiện ích" },
             new[] { "Admin" }),
        };

        private const int MaxSmartTools = 40;

        #endregion

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

        public void RepairHistoryAfterCancel()
        {
            if (_conversationHistory.Count > 0 &&
                _conversationHistory.Last() is AssistantChatMessage)
            {
                _conversationHistory.RemoveAt(_conversationHistory.Count - 1);
                _conversationHistory.Add(new AssistantChatMessage("(cancelled by user)"));
            }
        }

        public async Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default)
        {
            if (_client == null)
                throw new InvalidOperationException("OpenAI client not initialized. Please set your API key.");

            _lastUserMessage = userMessage;
            _conversationHistory.Add(new UserChatMessage(userMessage));
            TrimHistory();

            return await GetCompletionStreamingAsync(ct);
        }

        public async Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            if (toolResults != null)
            {
                foreach (var kvp in toolResults)
                    _conversationHistory.Add(new ToolChatMessage(kvp.Key, kvp.Value ?? ""));
            }

            return await GetCompletionStreamingAsync(ct);
        }

        private async Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> GetCompletionStreamingAsync(
            CancellationToken ct)
        {
            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = ConfigService.Load().MaxTokens,
            };

            var selectedTools = SelectToolsForMessage(_lastUserMessage);
            foreach (var tool in selectedTools)
                options.Tools.Add(tool);

            var messages = BuildMessages();

            var contentBuilder = new StringBuilder(512);
            var tcIds = new Dictionary<int, string>();
            var tcNames = new Dictionary<int, string>();
            var tcArgs = new Dictionary<int, StringBuilder>();
            ChatFinishReason? finishReason = null;

            await foreach (var update in _client.CompleteChatStreamingAsync(messages, options, ct))
            {
                foreach (var part in update.ContentUpdate)
                {
                    var token = part.Text;
                    if (token == null) continue;
                    contentBuilder.Append(token);
                    TokenReceived?.Invoke(token);
                }

                foreach (var tcUpdate in update.ToolCallUpdates)
                {
                    if (tcUpdate.ToolCallId != null)
                        tcIds[tcUpdate.Index] = tcUpdate.ToolCallId;
                    if (tcUpdate.FunctionName != null)
                        tcNames[tcUpdate.Index] = tcUpdate.FunctionName;
                    if (tcUpdate.FunctionArgumentsUpdate != null && !tcUpdate.FunctionArgumentsUpdate.ToMemory().IsEmpty)
                    {
                        if (!tcArgs.TryGetValue(tcUpdate.Index, out var sb))
                            tcArgs[tcUpdate.Index] = sb = new StringBuilder(256);
                        sb.Append(tcUpdate.FunctionArgumentsUpdate.ToString());
                    }
                }

                if (update.FinishReason.HasValue)
                    finishReason = update.FinishReason.Value;
            }

            DebugMessage?.Invoke($"OpenAI: finish={finishReason}, tool_calls={tcIds.Count}, tools_sent={selectedTools.Count}");

            if (finishReason == ChatFinishReason.ToolCalls && tcIds.Count > 0)
            {
                var chatToolCalls = new List<ChatToolCall>();
                var toolCalls = new List<Models.ToolCallRequest>();

                foreach (var idx in tcIds.Keys.OrderBy(x => x))
                {
                    var funcName = tcNames.GetValueOrDefault(idx, "");
                    if (string.IsNullOrEmpty(funcName)) continue;

                    var argsStr = tcArgs.TryGetValue(idx, out var sb) ? sb.ToString() : "{}";
                    chatToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                        tcIds[idx], funcName, BinaryData.FromString(argsStr)));
                    toolCalls.Add(new Models.ToolCallRequest
                    {
                        ToolCallId = tcIds[idx],
                        FunctionName = funcName,
                        Arguments = ParseArguments(argsStr)
                    });
                }

                if (chatToolCalls.Count > 0)
                {
                    var assistantMsg = new AssistantChatMessage(chatToolCalls);
                    if (contentBuilder.Length > 0)
                        assistantMsg.Content.Add(ChatMessageContentPart.CreateTextPart(contentBuilder.ToString()));
                    _conversationHistory.Add(assistantMsg);
                }

                return (null, toolCalls);
            }

            var text = contentBuilder.ToString();
            _conversationHistory.Add(new AssistantChatMessage(text));

            return (text, new List<Models.ToolCallRequest>());
        }

        #region Smart Tool Selection

        private IReadOnlyList<ChatTool> SelectToolsForMessage(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return _skillRegistry.GetAllToolDefinitions();

            var stripped = StripVietnameseDiacritics(userMessage.ToLowerInvariant());
            var matchedPacks = new HashSet<string> { "Core" };

            foreach (var (keywords, packs) in KeywordPackMap)
            {
                foreach (var kw in keywords)
                {
                    if (stripped.Contains(StripVietnameseDiacritics(kw)))
                    {
                        foreach (var pack in packs)
                            matchedPacks.Add(pack);
                        break;
                    }
                }
            }

            _lastPromptContext = PromptAnalyzer.Analyze(userMessage);
            InferPacksFromContext(_lastPromptContext, matchedPacks);

            if (matchedPacks.Count <= 1)
                return _skillRegistry.GetAllToolDefinitions();

            var tools = _skillRegistry.GetToolDefinitionsByPacks(matchedPacks);
            if (tools.Count > MaxSmartTools)
                return tools.Take(MaxSmartTools).ToList();

            return tools;
        }

        private static void InferPacksFromContext(PromptContext ctx, HashSet<string> packs)
        {
            var cat = ctx.DetectedCategory ?? "";
            bool isMep = cat.Contains("Duct") || cat.Contains("Pipe") || cat.Contains("Conduit")
                         || cat.Contains("Cable") || cat.Contains("Mechanical") || cat.Contains("Electrical")
                         || cat.Contains("Plumbing") || cat.Contains("Sprinkler") || cat.Contains("Air Terminal")
                         || cat.Contains("Fire") || cat.Contains("Lighting");

            if (isMep || ctx.DetectedSystem != null)
                packs.Add("MEP");

            if (ctx.PrimaryIntent == PromptIntent.Visual || ctx.PrimaryIntent == PromptIntent.Navigate)
                packs.Add("ViewControl");

            if (ctx.PrimaryIntent == PromptIntent.Check)
                packs.Add("BIMCoordinator");

            if (ctx.PrimaryIntent == PromptIntent.Tag)
                packs.Add("Modeler");
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

        #endregion

        private List<OaiMessage> BuildMessages()
        {
            var messages = new List<OaiMessage>
            {
                new SystemChatMessage(SystemPrompt)
            };

            var hint = _lastPromptContext?.ContextHint;
            if (!string.IsNullOrEmpty(hint))
                messages.Add(new SystemChatMessage(hint));

            messages.AddRange(_conversationHistory);
            return messages;
        }

        private void TrimHistory()
        {
            var config = ConfigService.Load();
            int maxMessages = config.MaxConversationMessages;
            const int maxHistoryTokens = 12000;
            const int keepRecentCount = 6;

            while (_conversationHistory.Count > maxMessages)
            {
                _conversationHistory.RemoveAt(0);
                while (_conversationHistory.Count > 0 && _conversationHistory[0] is ToolChatMessage)
                    _conversationHistory.RemoveAt(0);
            }

            while (_conversationHistory.Count > keepRecentCount && EstimateHistoryTokens() > maxHistoryTokens)
            {
                _conversationHistory.RemoveAt(0);
                while (_conversationHistory.Count > 0 && _conversationHistory[0] is ToolChatMessage)
                    _conversationHistory.RemoveAt(0);
            }
        }

        private int EstimateHistoryTokens()
        {
            int total = 0;
            foreach (var msg in _conversationHistory)
            {
                string text = null;
                if (msg is UserChatMessage um) text = um.Content?.FirstOrDefault()?.Text;
                else if (msg is AssistantChatMessage am) text = am.Content?.FirstOrDefault()?.Text;
                else if (msg is ToolChatMessage tm) text = tm.Content?.FirstOrDefault()?.Text;
                total += (text?.Length ?? 0) / 4;
            }
            return total;
        }

        public List<string> ValidateToolCalls(List<Models.ToolCallRequest> toolCalls)
            => new();

        public Task<(string assistantMessage, List<Models.ToolCallRequest> toolCalls)> RetryWithValidationErrorAsync(
            List<string> errors, CancellationToken ct = default)
            => Task.FromResult(("Validation not supported in this mode.", new List<Models.ToolCallRequest>()));

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
