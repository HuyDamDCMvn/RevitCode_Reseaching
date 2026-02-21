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
    public class OllamaChatService : RevitChat.Models.IChatService
    {
        private ChatClient _client;
        private readonly List<OaiMessage> _conversationHistory = new();
        private readonly SkillRegistry _skillRegistry;
        private HashSet<string> _allToolNames;
        private string _lastUserMessage = "";

        private string _toolMode = "smart";
        private List<string> _enabledPacks = new()
        {
            "Core", "ViewControl", "MEP", "Modeler", "BIMCoordinator", "LinkedModels"
        };

        #region CoreTools + KeywordToolMap (for Smart mode)

        private static readonly HashSet<string> CoreTools = new()
        {
            "get_elements", "count_elements", "get_element_parameters", "search_elements",
            "get_project_info", "get_levels", "get_categories", "get_current_view", "get_rooms",
            "set_parameter_value", "delete_elements", "select_elements", "rename_elements",
            "hide_elements", "unhide_elements", "isolate_elements", "reset_view_isolation",
            "override_element_color", "override_category_color", "get_current_selection",
            "zoom_to_elements", "isolate_by_level", "override_color_by_filter",
            "get_levels_detailed", "get_hidden_elements"
        };

        private static readonly Dictionary<string, string[]> KeywordToolMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["color|red|blue|green|yellow|orange|purple|pink|cyan|override color|tô màu|đổi màu"] = new[] {
                "override_element_color", "override_category_color", "reset_element_overrides",
                "set_element_transparency", "override_color_by_level", "override_color_by_filter"
            },
            ["level|floor|story|elevation|tầng|cao độ"] = new[] {
                "get_levels_detailed", "create_level", "duplicate_levels_offset",
                "rename_level", "delete_levels", "isolate_by_level", "hide_by_level",
                "override_color_by_level", "check_level_consistency"
            },
            ["hide|show|unhide|isolate|visible|visibility|ẩn|hiện|cô lập"] = new[] {
                "hide_elements", "unhide_elements", "isolate_elements", "isolate_category",
                "hide_category", "unhide_category", "reset_view_isolation", "get_hidden_elements",
                "isolate_by_level", "hide_by_level", "isolate_by_filter"
            },
            ["duct|pipe|mep|hvac|mechanical|electrical|plumbing|conduit|cable|ống|điện"] = new[] {
                "get_mep_systems", "get_system_elements", "get_duct_summary", "get_pipe_summary",
                "get_conduit_summary", "get_cable_tray_summary", "get_mechanical_equipment",
                "get_plumbing_fixtures", "get_electrical_equipment", "get_fittings",
                "check_disconnected_elements", "mep_quantity_takeoff"
            },
            ["space|zone|airflow|không gian|lưu lượng"] = new[] {
                "get_mep_spaces", "get_hvac_zones", "check_space_airflow", "get_unoccupied_spaces"
            },
            ["sheet|viewport|bản vẽ"] = new[] {
                "get_sheets_summary", "create_sheet", "place_view_on_sheet",
                "get_sheet_viewports", "remove_viewport"
            },
            ["copy|move|mirror|duplicate|di chuyển|sao chép"] = new[] {
                "copy_elements", "move_elements", "mirror_elements",
                "duplicate_views", "duplicate_sheets"
            },
            ["export|csv|boq|xuất"] = new[] {
                "export_to_csv", "mep_quantity_takeoff", "export_mep_boq"
            },
            ["tag|dimension|text|ghi chú|kích thước"] = new[] {
                "tag_elements", "get_untagged_elements", "tag_all_in_view", "add_text_note"
            },
            ["family|type|swap|load|place|họ"] = new[] {
                "get_family_types", "place_family_instance", "swap_family_type", "load_family"
            },
            ["group|nhóm"] = new[] {
                "get_groups", "create_group", "ungroup", "get_group_members", "place_group_instance"
            },
            ["material|vật liệu"] = new[] {
                "get_materials", "get_element_material", "set_element_material", "get_material_quantities"
            },
            ["filter|template|bộ lọc|mẫu"] = new[] {
                "get_view_filters", "get_view_templates", "apply_view_template",
                "create_parameter_filter", "get_filter_rules"
            },
            ["workset|phase|giai đoạn"] = new[] {
                "get_worksets", "move_to_workset", "get_phases", "get_elements_by_phase", "set_phase"
            },
            ["clash|clearance|overlap|va chạm"] = new[] {
                "check_clashes", "check_clearance", "find_overlapping", "get_clash_summary"
            },
            ["warning|health|purge|unused|cảnh báo|không dùng"] = new[] {
                "get_model_warnings", "get_warning_elements", "get_model_statistics",
                "find_imported_cad", "find_inplace_families", "find_unused_families",
                "get_purgeable_elements", "find_duplicate_types"
            },
            ["parameter|shared|tham số"] = new[] {
                "get_shared_parameters", "get_project_parameters", "check_parameter_values",
                "add_project_parameter", "get_parameter_bindings"
            },
            ["grid|lưới"] = new[] {
                "get_grids", "check_grid_alignment", "create_grid", "find_off_axis_elements"
            },
            ["room|area|boundary|finish|phòng|diện tích"] = new[] {
                "get_rooms_detailed", "get_room_boundaries", "get_room_finishes",
                "get_area_schemes", "get_unplaced_rooms", "get_redundant_rooms"
            },
            ["revision|markup|cloud|phát hành"] = new[] {
                "get_revisions", "get_revision_clouds", "add_revision",
                "get_sheets_by_revision", "get_revision_schedule"
            },
            ["link|linked|liên kết"] = new[] {
                "get_linked_models", "get_linked_elements", "count_linked_elements",
                "get_linked_element_parameters", "search_linked_elements", "get_link_types"
            },
            ["naming|audit|tên|kiểm tra tên"] = new[] {
                "audit_view_names", "audit_sheet_numbers", "audit_level_names",
                "audit_family_names", "audit_workset_names"
            },
            ["select by|filter by|chọn theo|lọc theo"] = new[] {
                "select_by_parameter_value", "select_by_bounding_box",
                "select_elements_in_view", "get_selection_summary"
            },
            ["coordination|report|phối hợp|báo cáo"] = new[] {
                "generate_clash_report", "compare_element_counts",
                "get_link_coordination_status", "get_scope_box_summary"
            },
            ["insulation|hanger|bảo ôn|giá đỡ"] = new[] {
                "get_insulation_quantities", "get_hanger_quantities"
            },
            ["schedule|bảng"] = new[] { "get_schedule_data" },
            ["transparency|trong suốt"] = new[] { "set_element_transparency" },
            ["zoom|phóng to"] = new[] { "zoom_to_elements" },
            ["selection|đang chọn"] = new[] { "get_current_selection" },
        };

        #endregion

        #region Dynamic Few-Shot Examples

        private static readonly List<(string[] keywords, string example)> FewShotExamples = new()
        {
            // Count / Query
            (new[] { "how many", "count", "bao nhiêu", "đếm", "số lượng" },
                "User: how many walls in the model?\nAssistant:\n<tool_call>\n{\"name\": \"count_elements\", \"arguments\": {\"category\": \"Walls\"}}\n</tool_call>"),
            (new[] { "how many", "count", "bao nhiêu", "duct", "ống" },
                "User: có bao nhiêu ống trong mô hình?\nAssistant:\n<tool_call>\n{\"name\": \"count_elements\", \"arguments\": {\"category\": \"Ducts\"}}\n</tool_call>"),
            (new[] { "list", "get", "show", "liệt kê", "xem", "hiển thị", "danh sách" },
                "User: liệt kê tất cả phòng trên tầng 1\nAssistant:\n<tool_call>\n{\"name\": \"get_rooms\", \"arguments\": {\"level\": \"Level 1\"}}\n</tool_call>"),
            (new[] { "parameter", "tham số", "thuộc tính", "property" },
                "User: show parameters of element 12345\nAssistant:\n<tool_call>\n{\"name\": \"get_element_parameters\", \"arguments\": {\"element_id\": 12345}}\n</tool_call>"),
            (new[] { "search", "find", "tìm", "tìm kiếm" },
                "User: tìm tất cả tường có vật liệu Concrete\nAssistant:\n<tool_call>\n{\"name\": \"search_elements\", \"arguments\": {\"category\": \"Walls\", \"param_name\": \"Material\", \"param_value\": \"Concrete\"}}\n</tool_call>"),

            // Color / Override
            (new[] { "color", "màu", "red", "đỏ", "blue", "xanh", "override" },
                "User: đổi màu tất cả duct sang đỏ\nAssistant:\n<tool_call>\n{\"name\": \"override_category_color\", \"arguments\": {\"category\": \"Ducts\", \"color\": \"Red\"}}\n</tool_call>"),
            (new[] { "color", "màu", "element", "phần tử" },
                "User: change color of element 54321 to blue\nAssistant:\n<tool_call>\n{\"name\": \"override_element_color\", \"arguments\": {\"element_ids\": [54321], \"color\": \"Blue\"}}\n</tool_call>"),
            (new[] { "transparency", "trong suốt" },
                "User: set transparency of walls to 50%\nAssistant:\n<tool_call>\n{\"name\": \"set_element_transparency\", \"arguments\": {\"category\": \"Walls\", \"transparency\": 50}}\n</tool_call>"),

            // Hide / Show / Isolate
            (new[] { "hide", "ẩn" },
                "User: ẩn tất cả pipe trong view hiện tại\nAssistant:\n<tool_call>\n{\"name\": \"hide_category\", \"arguments\": {\"category\": \"Pipes\"}}\n</tool_call>"),
            (new[] { "isolate", "cô lập" },
                "User: isolate all elements on Level 01\nAssistant:\n<tool_call>\n{\"name\": \"isolate_by_level\", \"arguments\": {\"level_name\": \"Level 01\"}}\n</tool_call>"),
            (new[] { "isolate", "cô lập", "category", "loại" },
                "User: cô lập tất cả duct\nAssistant:\n<tool_call>\n{\"name\": \"isolate_category\", \"arguments\": {\"category\": \"Ducts\"}}\n</tool_call>"),
            (new[] { "unhide", "hiện", "show all", "hiện tất cả", "reset" },
                "User: hiện lại tất cả element đã ẩn\nAssistant:\n<tool_call>\n{\"name\": \"reset_view_isolation\", \"arguments\": {}}\n</tool_call>"),

            // Level
            (new[] { "level", "tầng", "cao độ", "create level", "tạo level" },
                "User: tạo thêm một bộ level với khoảng cách +500 so với level cũ và thêm hậu tố _add\nAssistant:\n<tool_call>\n{\"name\": \"duplicate_levels_offset\", \"arguments\": {\"offset_mm\": 500, \"suffix\": \"_add\"}}\n</tool_call>"),
            (new[] { "level", "tầng", "list", "danh sách" },
                "User: show me all levels with elevations\nAssistant:\n<tool_call>\n{\"name\": \"get_levels_detailed\", \"arguments\": {}}\n</tool_call>"),

            // BOQ / Export
            (new[] { "boq", "quantity", "takeoff", "bóc tách", "khối lượng" },
                "User: tạo BOQ cho duct\nAssistant:\n<tool_call>\n{\"name\": \"mep_quantity_takeoff\", \"arguments\": {\"categories\": [\"Ducts\"]}}\n</tool_call>"),
            (new[] { "export", "csv", "xuất" },
                "User: export all walls to CSV\nAssistant:\n<tool_call>\n{\"name\": \"export_to_csv\", \"arguments\": {\"category\": \"Walls\", \"file_path\": \"C:\\\\temp\\\\walls.csv\"}}\n</tool_call>"),

            // Modify
            (new[] { "rename", "đổi tên" },
                "User: đổi tên tất cả view có chứa 'Draft' thành 'Final'\nAssistant:\n<tool_call>\n{\"name\": \"rename_elements\", \"arguments\": {\"category\": \"Views\", \"old_text\": \"Draft\", \"new_text\": \"Final\"}}\n</tool_call>"),
            (new[] { "delete", "xóa" },
                "User: delete elements 111, 222, 333\nAssistant:\n<tool_call>\n{\"name\": \"delete_elements\", \"arguments\": {\"element_ids\": [111, 222, 333]}}\n</tool_call>"),
            (new[] { "set", "đặt", "parameter", "tham số", "value", "giá trị" },
                "User: set Mark parameter of element 999 to \"ABC\"\nAssistant:\n<tool_call>\n{\"name\": \"set_parameter_value\", \"arguments\": {\"element_id\": 999, \"param_name\": \"Mark\", \"value\": \"ABC\"}}\n</tool_call>"),
            (new[] { "copy", "sao chép" },
                "User: copy elements 100, 200 by offset (1000, 0, 0) mm\nAssistant:\n<tool_call>\n{\"name\": \"copy_elements\", \"arguments\": {\"element_ids\": [100, 200], \"offset_x_mm\": 1000, \"offset_y_mm\": 0, \"offset_z_mm\": 0}}\n</tool_call>"),
            (new[] { "move", "di chuyển" },
                "User: di chuyển element 300 theo hướng (500, 0, 0)\nAssistant:\n<tool_call>\n{\"name\": \"move_elements\", \"arguments\": {\"element_ids\": [300], \"offset_x_mm\": 500, \"offset_y_mm\": 0, \"offset_z_mm\": 0}}\n</tool_call>"),

            // MEP
            (new[] { "duct", "pipe", "mep", "system", "hệ thống", "ống" },
                "User: show all MEP systems\nAssistant:\n<tool_call>\n{\"name\": \"get_mep_systems\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "disconnect", "ngắt kết nối", "check" },
                "User: kiểm tra các phần tử bị ngắt kết nối\nAssistant:\n<tool_call>\n{\"name\": \"check_disconnected_elements\", \"arguments\": {}}\n</tool_call>"),

            // Sheet / View
            (new[] { "sheet", "bản vẽ" },
                "User: liệt kê tất cả sheet\nAssistant:\n<tool_call>\n{\"name\": \"get_sheets_summary\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "view", "template" },
                "User: list all view templates\nAssistant:\n<tool_call>\n{\"name\": \"get_view_templates\", \"arguments\": {}}\n</tool_call>"),

            // Family / Type
            (new[] { "family", "type", "họ", "loại" },
                "User: liệt kê tất cả family type của Door\nAssistant:\n<tool_call>\n{\"name\": \"get_family_types\", \"arguments\": {\"category\": \"Doors\"}}\n</tool_call>"),
            (new[] { "place", "đặt", "family" },
                "User: place a door at point (0,0,0)\nAssistant:\n<tool_call>\n{\"name\": \"place_family_instance\", \"arguments\": {\"family_name\": \"Door\", \"x_mm\": 0, \"y_mm\": 0, \"z_mm\": 0}}\n</tool_call>"),

            // Health / Warning / Audit
            (new[] { "warning", "cảnh báo" },
                "User: có bao nhiêu warning trong model?\nAssistant:\n<tool_call>\n{\"name\": \"get_model_warnings\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "unused", "không dùng", "purge", "dọn dẹp" },
                "User: tìm các family không sử dụng\nAssistant:\n<tool_call>\n{\"name\": \"find_unused_families\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "health", "statistics", "thống kê", "sức khỏe" },
                "User: model health statistics\nAssistant:\n<tool_call>\n{\"name\": \"get_model_statistics\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "clash", "va chạm", "overlap" },
                "User: check clashes between Ducts and Pipes\nAssistant:\n<tool_call>\n{\"name\": \"check_clashes\", \"arguments\": {\"category_a\": \"Ducts\", \"category_b\": \"Pipes\"}}\n</tool_call>"),

            // Room / Area
            (new[] { "room", "phòng" },
                "User: liệt kê tất cả phòng với diện tích\nAssistant:\n<tool_call>\n{\"name\": \"get_rooms_detailed\", \"arguments\": {}}\n</tool_call>"),

            // Grid
            (new[] { "grid", "lưới" },
                "User: show all grids\nAssistant:\n<tool_call>\n{\"name\": \"get_grids\", \"arguments\": {}}\n</tool_call>"),

            // Material
            (new[] { "material", "vật liệu" },
                "User: liệt kê tất cả vật liệu\nAssistant:\n<tool_call>\n{\"name\": \"get_materials\", \"arguments\": {}}\n</tool_call>"),

            // Link
            (new[] { "link", "liên kết" },
                "User: show all linked models\nAssistant:\n<tool_call>\n{\"name\": \"get_linked_models\", \"arguments\": {}}\n</tool_call>"),

            // Select
            (new[] { "select", "chọn" },
                "User: chọn tất cả wall trên Level 1\nAssistant:\n<tool_call>\n{\"name\": \"select_elements\", \"arguments\": {\"category\": \"Walls\", \"level\": \"Level 1\"}}\n</tool_call>"),
            (new[] { "zoom", "phóng to", "focus" },
                "User: zoom to elements 100, 200\nAssistant:\n<tool_call>\n{\"name\": \"zoom_to_elements\", \"arguments\": {\"element_ids\": [100, 200]}}\n</tool_call>"),

            // Project info
            (new[] { "project", "dự án", "info", "thông tin" },
                "User: thông tin dự án\nAssistant:\n<tool_call>\n{\"name\": \"get_project_info\", \"arguments\": {}}\n</tool_call>"),

            // Workset / Phase
            (new[] { "workset" },
                "User: list all worksets\nAssistant:\n<tool_call>\n{\"name\": \"get_worksets\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "phase", "giai đoạn" },
                "User: list all phases\nAssistant:\n<tool_call>\n{\"name\": \"get_phases\", \"arguments\": {}}\n</tool_call>"),

            // Tag
            (new[] { "tag", "ghi chú", "untagged" },
                "User: find untagged elements in current view\nAssistant:\n<tool_call>\n{\"name\": \"get_untagged_elements\", \"arguments\": {}}\n</tool_call>"),

            // Group
            (new[] { "group", "nhóm" },
                "User: liệt kê tất cả group\nAssistant:\n<tool_call>\n{\"name\": \"get_groups\", \"arguments\": {}}\n</tool_call>"),

            // Filter
            (new[] { "filter", "bộ lọc" },
                "User: list all view filters\nAssistant:\n<tool_call>\n{\"name\": \"get_view_filters\", \"arguments\": {}}\n</tool_call>"),

            // Revision
            (new[] { "revision", "phát hành" },
                "User: list all revisions\nAssistant:\n<tool_call>\n{\"name\": \"get_revisions\", \"arguments\": {}}\n</tool_call>"),
        };

        private const int MaxFewShotExamples = 5;

        private string BuildDynamicExamples(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return "";

            var lower = userMessage.ToLowerInvariant();
            var scored = new List<(int score, string example)>();

            foreach (var (keywords, example) in FewShotExamples)
            {
                int score = 0;
                foreach (var kw in keywords)
                {
                    if (lower.Contains(kw))
                        score++;
                }
                if (score > 0)
                    scored.Add((score, example));
            }

            if (scored.Count == 0) return "";

            var selected = scored
                .OrderByDescending(s => s.score)
                .Take(MaxFewShotExamples)
                .Select(s => s.example);

            var sb = new StringBuilder();
            foreach (var ex in selected)
            {
                sb.AppendLine(ex);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        #endregion

        public OllamaChatService(SkillRegistry skillRegistry)
        {
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
            _lastUserMessage = "";
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> SendMessageAsync(
            string userMessage, CancellationToken ct = default)
        {
            if (_client == null)
                throw new InvalidOperationException("Ollama client not initialized.");

            _lastUserMessage = userMessage;
            _conversationHistory.Add(new UserChatMessage(userMessage));
            TrimHistory();

            return await GetCompletionAsync(ct);
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tool results:");
            int totalChars = 0;
            foreach (var kvp in toolResults)
            {
                var val = kvp.Value ?? "";
                totalChars += val.Length;
                sb.AppendLine($"[{kvp.Key}]: {val}");
            }
            sb.AppendLine();

            if (totalChars > 6000)
                sb.AppendLine("NOTE: The data above is large. Provide a concise summary to the user. Do NOT repeat the raw data.");

            sb.AppendLine("Analyze the results and answer the user. If you need more data, output ONE <tool_call>. Otherwise respond directly with NO <tool_call> tags.");

            _conversationHistory.Add(new UserChatMessage(sb.ToString()));

            return await GetCompletionAsync(ct);
        }

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionAsync(
            CancellationToken ct)
        {
            if (_toolMode == "twostage")
                return await GetCompletionTwoStageAsync(ct);

            var config = LocalConfigService.Load();
            var options = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };

            var messages = BuildMessages();
            var response = await _client.CompleteChatAsync(messages, options, ct);
            var text = StripQwenTokens(response.Value.Content?.FirstOrDefault()?.Text ?? "");

            return ProcessResponse(text);
        }

        #region Two-Stage Mode

        private async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> GetCompletionTwoStageAsync(
            CancellationToken ct)
        {
            var config = LocalConfigService.Load();
            var options = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };

            // Stage 1: ask LLM to pick tool names from the full list
            var stage1Messages = BuildTwoStageSelectionMessages();
            var stage1Response = await _client.CompleteChatAsync(stage1Messages, options, ct);
            var stage1Text = StripQwenTokens(stage1Response.Value.Content?.FirstOrDefault()?.Text ?? "");

            var selectedTools = ParseSelectedToolNames(stage1Text);

            if (selectedTools.Count == 0)
                selectedTools = CoreTools.ToList();

            // Stage 2: send the conversation with only the selected tools
            var stage2Messages = BuildMessages(selectedTools);
            var stage2Response = await _client.CompleteChatAsync(stage2Messages, options, ct);
            var stage2Text = StripQwenTokens(stage2Response.Value.Content?.FirstOrDefault()?.Text ?? "");

            return ProcessResponse(stage2Text);
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
                // Smart mode: CoreTools + keyword match
                selected = new HashSet<string>(CoreTools);
                var combined = userMessage.ToLowerInvariant();
                foreach (var kvp in KeywordToolMap)
                {
                    var keywords = kvp.Key.Split('|');
                    if (keywords.Any(k => combined.Contains(k)))
                    {
                        foreach (var toolName in kvp.Value)
                            selected.Add(toolName);
                    }
                }
            }

            var sb = new StringBuilder();
            foreach (var toolName in selected)
            {
                if (toolIndex.TryGetValue(toolName, out var tool))
                    sb.AppendLine($"- {tool.FunctionName}: {tool.FunctionDescription}");
            }

            return sb.ToString();
        }

        #endregion

        private (string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls) ProcessResponse(string text)
        {
            var toolCalls = ExtractToolCalls(text);
            if (toolCalls.Count > 0)
            {
                var cleanText = RemoveToolCallTags(text).Trim();
                _conversationHistory.Add(new AssistantChatMessage(
                    !string.IsNullOrEmpty(cleanText) ? cleanText : $"[Executing {toolCalls.Count} tool(s)...]"));

                return (null, toolCalls);
            }

            _conversationHistory.Add(new AssistantChatMessage(text));
            return (text, new List<RevitChat.Models.ToolCallRequest>());
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

            var tagPattern = new Regex(
                @"<tool_call>\s*(\{.+?\})\s*</tool_call>",
                RegexOptions.Singleline);

            foreach (Match match in tagPattern.Matches(stripped))
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

        private string RemoveToolCallTags(string text)
        {
            text = Regex.Replace(text, @"<tool_call>.*?</tool_call>", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"```json?\s*\{[^`]*?""name""[^`]*?\}\s*```", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"```json?\s*<tool_call>.*?</tool_call>\s*```", "", RegexOptions.Singleline);
            return text.Trim();
        }

        private string BuildSystemPrompt(List<string> forcedTools = null)
        {
            var userMsg = _conversationHistory.Count > 0 ? _lastUserMessage : "";
            var catalog = BuildToolCatalogForMessage(userMsg, forcedTools);
            var dynamicExamples = BuildDynamicExamples(userMsg);

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

            return $@"You are a Revit BIM assistant. You execute tools to get data from the Revit model.
You understand both English and Vietnamese.

## AVAILABLE TOOLS
{catalog}

## FORMAT
To call a tool, output EXACTLY this (no code fences, no extra text after it):

<tool_call>
{{""name"": ""tool_name"", ""arguments"": {{""param1"": ""value1""}}}}
</tool_call>

## RULES
1. When the user asks about model data, output a <tool_call> immediately. Do NOT describe what you will do.
2. Output ONLY ONE <tool_call> per response. Stop writing after </tool_call>.
3. Do NOT wrap <tool_call> in ```json``` code blocks.
4. The ""arguments"" field MUST be a JSON object with the tool's parameters inside it.
5. After receiving tool results, answer the user directly. Do NOT output another <tool_call> unless you need more data.
6. For destructive operations (delete, modify), confirm with the user FIRST before calling the tool.
7. NEVER invent data. Only use tool results.
8. Reply in the same language the user uses.
9. Vietnamese category mapping: tường=Walls, cửa=Doors, cửa sổ=Windows, ống=Ducts/Pipes, phòng=Rooms, sàn=Floors, cột=Columns, dầm=Structural Framing, trần=Ceilings, mái=Roofs, cầu thang=Stairs, lan can=Railings, thiết bị vệ sinh=Plumbing Fixtures, khay cáp=Cable Trays, ống dẫn=Conduits, đèn=Lighting Fixtures.

## WRONG (do NOT do this):
```json
<tool_call>
{{""name"": ""get_walls"", ""category"": ""Walls""}}
</tool_call>
```

## CORRECT:
<tool_call>
{{""name"": ""count_elements"", ""arguments"": {{""category"": ""Walls""}}}}
</tool_call>

{examplesSection}";
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
