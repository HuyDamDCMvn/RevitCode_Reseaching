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
        private bool _isContinuation;

        public event Action<string> DebugMessage;

        private string _toolMode = "smart";
        private List<string> _enabledPacks = new()
        {
            "Core", "ViewControl", "MEP", "Modeler", "BIMCoordinator", "LinkedModels"
        };

        #region CoreTools + KeywordGroups (for Smart mode)

        private class KeywordGroup
        {
            public string Name { get; init; }
            public string[] Keywords { get; init; }
            public string[] Tools { get; init; }
            public int Weight { get; init; } = 1;
        }

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

        private static readonly List<KeywordGroup> KeywordGroups = new()
        {
            new KeywordGroup
            {
                Name = "Color / Override",
                Weight = 2,
                Keywords = new[] { "color", "red", "blue", "green", "yellow", "orange", "purple", "pink", "cyan", "override color", "tô màu", "đổi màu" },
                Tools = new[] { "override_element_color", "override_category_color", "reset_element_overrides", "set_element_transparency", "override_color_by_level", "override_color_by_filter" }
            },
            new KeywordGroup
            {
                Name = "Levels",
                Keywords = new[] { "level", "floor", "story", "elevation", "tầng", "cao độ" },
                Tools = new[] { "get_levels_detailed", "create_level", "duplicate_levels_offset", "rename_level", "delete_levels", "isolate_by_level", "hide_by_level", "override_color_by_level", "check_level_consistency" }
            },
            new KeywordGroup
            {
                Name = "Visibility / Isolate",
                Weight = 2,
                Keywords = new[] { "hide", "show", "unhide", "isolate", "visible", "visibility", "ẩn", "hiện", "cô lập" },
                Tools = new[] { "hide_elements", "unhide_elements", "isolate_elements", "isolate_category", "hide_category", "unhide_category", "reset_view_isolation", "get_hidden_elements", "isolate_by_level", "hide_by_level", "isolate_by_filter" }
            },
            new KeywordGroup
            {
                Name = "MEP",
                Keywords = new[] { "duct", "pipe", "mep", "hvac", "mechanical", "electrical", "plumbing", "conduit", "cable", "ống", "điện", "fire", "sprinkler", "cháy" },
                Tools = new[] { "get_mep_systems", "get_system_elements", "get_duct_summary", "get_pipe_summary", "get_conduit_summary", "get_cable_tray_summary", "get_mechanical_equipment", "get_plumbing_fixtures", "get_electrical_equipment", "get_fire_protection_equipment", "get_fittings", "check_disconnected_elements", "mep_quantity_takeoff", "get_connector_info", "get_system_connectivity" }
            },
            new KeywordGroup
            {
                Name = "Space / Zone",
                Keywords = new[] { "space", "zone", "airflow", "không gian", "lưu lượng" },
                Tools = new[] { "get_mep_spaces", "get_hvac_zones", "check_space_airflow", "get_unoccupied_spaces" }
            },
            new KeywordGroup
            {
                Name = "Sheet / Viewport",
                Keywords = new[] { "sheet", "viewport", "bản vẽ" },
                Tools = new[] { "get_sheets_summary", "create_sheet", "place_view_on_sheet", "get_sheet_viewports", "remove_viewport" }
            },
            new KeywordGroup
            {
                Name = "Copy / Move / Mirror",
                Keywords = new[] { "copy", "move", "mirror", "duplicate", "di chuyển", "sao chép" },
                Tools = new[] { "copy_elements", "move_elements", "mirror_elements", "duplicate_views", "duplicate_sheets" }
            },
            new KeywordGroup
            {
                Name = "Export / BOQ",
                Keywords = new[] { "export", "csv", "boq", "xuất" },
                Tools = new[] { "export_to_csv", "mep_quantity_takeoff", "export_mep_boq" }
            },
            new KeywordGroup
            {
                Name = "Tag / Dimension",
                Keywords = new[] { "tag", "dimension", "text", "ghi chú", "kích thước" },
                Tools = new[] { "tag_elements", "get_untagged_elements", "tag_all_in_view", "add_text_note" }
            },
            new KeywordGroup
            {
                Name = "Family / Type",
                Keywords = new[] { "family", "type", "swap", "load", "place", "họ" },
                Tools = new[] { "get_family_types", "place_family_instance", "swap_family_type", "load_family" }
            },
            new KeywordGroup
            {
                Name = "Group",
                Keywords = new[] { "group", "nhóm" },
                Tools = new[] { "get_groups", "create_group", "ungroup", "get_group_members", "place_group_instance" }
            },
            new KeywordGroup
            {
                Name = "Material",
                Keywords = new[] { "material", "vật liệu" },
                Tools = new[] { "get_materials", "get_element_material", "set_element_material", "get_material_quantities" }
            },
            new KeywordGroup
            {
                Name = "Filter / Template",
                Keywords = new[] { "filter", "template", "bộ lọc", "mẫu" },
                Tools = new[] { "get_view_filters", "get_view_templates", "apply_view_template", "create_parameter_filter", "get_filter_rules" }
            },
            new KeywordGroup
            {
                Name = "Workset / Phase",
                Keywords = new[] { "workset", "phase", "giai đoạn" },
                Tools = new[] { "get_worksets", "move_to_workset", "get_phases", "get_elements_by_phase", "set_phase" }
            },
            new KeywordGroup
            {
                Name = "MEP Validation",
                Keywords = new[] { "missing", "oversized", "elevation", "validate", "thiếu", "quá cỡ", "slope", "độ dốc" },
                Tools = new[] { "check_missing_parameters", "check_elevation_conflicts", "check_oversized_elements", "get_warnings_mep", "check_pipe_slope" }
            },
            new KeywordGroup
            {
                Name = "Connectivity / Trace",
                Keywords = new[] { "connect", "connector", "trace", "path", "route", "kết nối", "đường đi", "đường ống" },
                Tools = new[] { "get_connector_info", "get_system_connectivity" }
            },
            new KeywordGroup
            {
                Name = "Size / Resize",
                Keywords = new[] { "size", "resize", "diameter", "width", "height", "đổi size", "kích thước", "đường kính" },
                Tools = new[] { "resize_mep_elements" }
            },
            new KeywordGroup
            {
                Name = "Clash / Clearance",
                Keywords = new[] { "clash", "clearance", "overlap", "va chạm" },
                Tools = new[] { "check_clashes", "check_clearance", "find_overlapping", "get_clash_summary" }
            },
            new KeywordGroup
            {
                Name = "Health / Purge",
                Keywords = new[] { "warning", "health", "purge", "unused", "cảnh báo", "không dùng", "detail level", "design option", "unresolved" },
                Tools = new[] { "get_model_warnings", "get_warning_elements", "get_model_statistics", "find_imported_cad", "find_inplace_families", "find_unused_families", "get_purgeable_elements", "find_duplicate_types", "find_unresolved_references", "get_design_options", "audit_detail_levels" }
            },
            new KeywordGroup
            {
                Name = "Parameters",
                Keywords = new[] { "parameter", "shared", "tham số" },
                Tools = new[] { "get_shared_parameters", "get_project_parameters", "check_parameter_values", "add_project_parameter", "get_parameter_bindings" }
            },
            new KeywordGroup
            {
                Name = "Grid",
                Keywords = new[] { "grid", "lưới" },
                Tools = new[] { "get_grids", "check_grid_alignment", "create_grid", "find_off_axis_elements" }
            },
            new KeywordGroup
            {
                Name = "Room / Area",
                Keywords = new[] { "room", "area", "boundary", "finish", "phòng", "diện tích" },
                Tools = new[] { "get_rooms_detailed", "get_room_boundaries", "get_room_finishes", "get_area_schemes", "get_unplaced_rooms", "get_redundant_rooms" }
            },
            new KeywordGroup
            {
                Name = "Revision",
                Keywords = new[] { "revision", "markup", "cloud", "phát hành" },
                Tools = new[] { "get_revisions", "get_revision_clouds", "add_revision", "get_sheets_by_revision", "get_revision_schedule" }
            },
            new KeywordGroup
            {
                Name = "Links",
                Keywords = new[] { "link", "linked", "liên kết" },
                Tools = new[] { "get_linked_models", "get_linked_elements", "count_linked_elements", "get_linked_element_parameters", "search_linked_elements", "get_link_types" }
            },
            new KeywordGroup
            {
                Name = "Naming Audit",
                Keywords = new[] { "naming", "audit", "tên", "kiểm tra tên" },
                Tools = new[] { "audit_view_names", "audit_sheet_numbers", "audit_level_names", "audit_family_names", "audit_workset_names" }
            },
            new KeywordGroup
            {
                Name = "Select / Filter",
                Keywords = new[] { "select by", "filter by", "chọn theo", "lọc theo" },
                Tools = new[] { "select_by_parameter_value", "select_by_bounding_box", "select_elements_in_view", "get_selection_summary" }
            },
            new KeywordGroup
            {
                Name = "Coordination Report",
                Keywords = new[] { "coordination", "report", "phối hợp", "báo cáo" },
                Tools = new[] { "generate_clash_report", "compare_element_counts", "get_link_coordination_status", "get_scope_box_summary" }
            },
            new KeywordGroup
            {
                Name = "Insulation / Hanger",
                Keywords = new[] { "insulation", "hanger", "bảo ôn", "giá đỡ" },
                Tools = new[] { "get_insulation_quantities", "get_hanger_quantities" }
            },
            new KeywordGroup
            {
                Name = "Schedule",
                Keywords = new[] { "schedule", "bảng", "field", "fields", "column", "columns" },
                Tools = new[] { "get_schedule_data", "create_schedule", "get_schedule_fields" }
            },
            new KeywordGroup
            {
                Name = "Transparency",
                Keywords = new[] { "transparency", "trong suốt" },
                Tools = new[] { "set_element_transparency" }
            },
            new KeywordGroup
            {
                Name = "Zoom",
                Keywords = new[] { "zoom", "phóng to" },
                Tools = new[] { "zoom_to_elements" }
            },
            new KeywordGroup
            {
                Name = "Selection",
                Keywords = new[] { "selection", "đang chọn" },
                Tools = new[] { "get_current_selection" }
            },
        };

        private static readonly List<(string from, string to)> NormalizationMap = new()
        {
            ("thiết bị vệ sinh", "plumbing fixtures"),
            ("máy điều hòa", "air conditioning"),
            ("bình nóng lạnh", "water heaters"),
            ("cầu thang", "stairs"),
            ("cửa sổ", "windows"),
            ("lan can", "railings"),
            ("khay cáp", "cable trays"),
            ("ống dẫn", "conduits"),
            ("thang máy", "elevators"),
            ("tường", "walls"),
            ("cửa", "doors"),
            ("sàn", "floors"),
            ("trần", "ceilings"),
            ("phòng", "rooms"),
            ("cột", "columns"),
            ("dầm", "beams"),
            ("mái", "roofs"),
            ("ống", "pipes"),
            ("đèn", "lighting fixtures"),
            ("quạt", "fans"),
            ("bơm", "pumps"),
            ("van", "valves"),
            ("bồn", "sinks"),
        };

        private static readonly string[] ActionKeywords = new[]
        {
            "count", "how many", "list", "show", "get", "export", "rename", "delete", "move",
            "copy", "mirror", "create", "update", "set", "override", "change", "modify", "hide",
            "unhide", "isolate", "select", "zoom", "place", "check", "audit", "report", "compare",
            "trace", "connect", "connector", "slope", "resize", "schedule", "fields", "columns", "preview", "dry run",
            "đếm", "liệt kê",
            "hiển thị", "xem", "xuất", "đổi tên", "xóa", "di chuyển", "sao chép", "tạo",
            "cập nhật", "đặt", "tô màu", "thay đổi", "ẩn", "hiện", "cô lập", "chọn", "xem trước",
            "phóng to", "đặt", "kiểm tra", "báo cáo", "so sánh", "độ dốc", "kích thước", "schedule"
        };

        private static readonly Dictionary<string, string> ToolSchemaHints = new()
        {
            ["get_elements"] = "category?, level?, view_name?, limit?",
            ["count_elements"] = "category?, level?, view_name?",
            ["search_elements"] = "category, param_name, param_value",
            ["get_element_parameters"] = "element_id (integer)",
            ["set_parameter_value"] = "element_ids (array), param_name, value, dry_run?",
            ["delete_elements"] = "element_ids (array), dry_run?",
            ["rename_elements"] = "element_ids (array), new_name, param_name? (default Mark), dry_run?",
            ["select_elements"] = "element_ids (array) — use get_elements first to get IDs",
            ["hide_elements"] = "element_ids (array), dry_run?",
            ["unhide_elements"] = "element_ids (array), dry_run?",
            ["isolate_elements"] = "element_ids (array), dry_run?",
            ["override_element_color"] = "element_ids (array), color, dry_run?",
            ["override_category_color"] = "category, color, dry_run?",
            ["set_element_transparency"] = "element_ids (array), transparency (0-100), dry_run?",
            ["get_current_selection"] = "no args",
            ["get_current_view"] = "no args",
            ["zoom_to_elements"] = "element_ids (array)",
            ["isolate_by_level"] = "level_name (exact name from model), dry_run?",
            ["override_color_by_filter"] = "level_name?, category?, param_name?, param_value?, color, dry_run?",
            ["export_to_csv"] = "category, param_names (array of parameter names for columns), file_path?, level?",
            ["export_mep_boq"] = "categories (array), file_path? — export BOQ to CSV file",
            ["get_model_warnings"] = "no args — all warnings in model",
            ["get_warning_elements"] = "text (filter), limit? — elements involved in specific warnings",
            ["get_warnings_mep"] = "no args — MEP-specific warnings only",
            ["get_rooms"] = "level? — basic room list",
            ["get_rooms_detailed"] = "level_name?, department?, limit? — detailed room data with areas",
            ["copy_elements"] = "element_ids (array), offset_x, offset_y, offset_z (in feet), dry_run?",
            ["move_elements"] = "element_ids (array), offset_x, offset_y, offset_z (in feet), dry_run?",
            ["mirror_elements"] = "element_ids (array), axis, dry_run?",
            ["place_family_instance"] = "family_type_id (integer), x, y, z? (feet), level_name?, dry_run?",
            ["mep_quantity_takeoff"] = "categories (array of strings)",
            ["check_clashes"] = "category_a, category_b, limit?",
            ["check_clearance"] = "category_a, category_b, min_distance_feet, limit?",
            ["create_sheet"] = "sheet_number, sheet_name, titleblock_type_id?, dry_run?",
            ["place_view_on_sheet"] = "sheet_id, view_id, x?, y?, dry_run?",
            ["remove_viewport"] = "viewport_id, dry_run?",
            ["create_level"] = "name, elevation, unit? (mm|ft, default mm), dry_run?",
            ["select_by_parameter_value"] = "category, parameter_name, value, match_type?",
            ["get_connector_info"] = "element_id (integer)",
            ["get_system_connectivity"] = "element_id, system_name?, max_depth?, max_elements?",
            ["check_pipe_slope"] = "system_name?, level?, min_slope_pct?, max_slope_pct?, limit?",
            ["resize_mep_elements"] = "element_ids (array), width_mm?, height_mm?, diameter_mm?, dry_run?",
            ["create_schedule"] = "category, field_names (array), schedule_name?, if_exists?, dry_run?",
            ["get_schedule_fields"] = "category",
            ["apply_view_template"] = "view_ids (array), template_id, dry_run?",
            ["create_parameter_filter"] = "filter_name, categories (array), parameter_name, rule_type, value, dry_run?",
            ["move_to_workset"] = "element_ids (array), workset_name, dry_run?",
            ["set_phase"] = "element_ids (array), phase_name, phase_type?, dry_run?",
            ["add_project_parameter"] = "parameter_name, categories (array), group?, is_instance?, data_type?, dry_run?",
            ["add_revision"] = "description, date?, issued_to?, issued_by?, dry_run?",
            ["duplicate_views"] = "view_ids (array), suffix?, duplicate_option?, dry_run?",
            ["duplicate_sheets"] = "sheet_ids (array), new_numbers?, dry_run?",
            ["create_grid"] = "name, start_x, start_y, end_x, end_y, dry_run?",
            ["rename_level"] = "level_id, new_name, dry_run?",
            ["delete_levels"] = "level_ids (array), dry_run?",
            ["duplicate_levels_offset"] = "offset_mm, suffix?, prefix?, dry_run?",
            ["set_element_material"] = "element_ids (array), material_id, dry_run?",
            ["tag_elements"] = "element_ids (array), tag_type_id?, add_leader?, tag_orientation?, dry_run?",
            ["tag_all_in_view"] = "category, add_leader?, dry_run?",
            ["add_text_note"] = "text, x, y, text_type_id?, dry_run?",
            ["create_group"] = "element_ids (array), group_name?, dry_run?",
            ["ungroup"] = "group_ids (array), dry_run?",
            ["place_group_instance"] = "group_type_id, x, y, z?, dry_run?",
            ["load_family"] = "file_path, dry_run?",
            ["swap_family_type"] = "element_ids (array), new_type_id, dry_run?",
            ["reset_element_overrides"] = "element_ids (array), dry_run?",
            ["reset_view_isolation"] = "dry_run?",
            ["hide_category"] = "category, dry_run?",
            ["unhide_category"] = "category, dry_run?",
            ["override_color_by_level"] = "level_name, color, dry_run?",
            ["isolate_by_filter"] = "level_name?, category?, param_name?, param_value?, dry_run?",
            ["hide_by_level"] = "level_name, dry_run?",

            // ProjectInfo (read-only)
            ["get_project_info"] = "no args",
            ["get_levels"] = "no args",
            ["get_categories"] = "no args",
            ["get_schedule_data"] = "schedule_name",

            // MepSystemAnalysis (read-only)
            ["get_mep_systems"] = "domain? (HVAC|Piping|Electrical)",
            ["get_system_elements"] = "system_name, limit?",
            ["get_duct_summary"] = "level?, system_name?",
            ["get_pipe_summary"] = "level?, system_name?",
            ["get_conduit_summary"] = "level?",
            ["get_cable_tray_summary"] = "level?",

            // MepEquipment (read-only)
            ["get_mechanical_equipment"] = "level?, limit?",
            ["get_plumbing_fixtures"] = "level?, limit?",
            ["get_electrical_equipment"] = "level?, limit?",
            ["get_fire_protection_equipment"] = "level?, limit?",
            ["get_fittings"] = "category? (Duct Fittings|Pipe Fittings), level?, limit?",

            // MepSpace (read-only)
            ["get_mep_spaces"] = "level?, limit?",
            ["get_hvac_zones"] = "limit?",
            ["check_space_airflow"] = "level?, limit?",
            ["get_unoccupied_spaces"] = "level?, limit?",

            // MepQuantityTakeoff (read-only)
            ["get_insulation_quantities"] = "category?, level?",
            ["get_hanger_quantities"] = "category?, level?",

            // MepValidation (read-only)
            ["check_disconnected_elements"] = "category?, level?, limit?",
            ["check_missing_parameters"] = "category, param_names (array), level?, limit?",
            ["check_elevation_conflicts"] = "category?, tolerance_mm?, limit?",
            ["check_oversized_elements"] = "category?, max_size_mm?, limit?",

            // FilterTemplate (read-only)
            ["get_view_filters"] = "view_id?",
            ["get_view_templates"] = "type_filter?",
            ["get_filter_rules"] = "filter_id",

            // WorksetPhase (read-only)
            ["get_worksets"] = "include_counts?",
            ["get_phases"] = "no args",
            ["get_elements_by_phase"] = "phase_name, phase_status?, limit?",

            // SheetManagement (read-only)
            ["get_sheets_summary"] = "limit?",
            ["get_sheet_viewports"] = "sheet_id",

            // Group (read-only)
            ["get_groups"] = "limit?",
            ["get_group_members"] = "group_id",

            // DimensionTag (read-only)
            ["get_untagged_elements"] = "category, limit?",

            // GridLevel (read-only)
            ["get_grids"] = "no args",
            ["check_grid_alignment"] = "tolerance_mm?",
            ["get_levels_detailed"] = "no args",
            ["check_level_consistency"] = "no args",
            ["find_off_axis_elements"] = "category?, tolerance_degrees?, limit?",

            // RoomArea (read-only)
            ["get_room_boundaries"] = "room_id",
            ["get_room_finishes"] = "level?, limit?",
            ["get_area_schemes"] = "no args",
            ["get_unplaced_rooms"] = "limit?",
            ["get_redundant_rooms"] = "limit?",

            // RevisionMarkup (read-only)
            ["get_revisions"] = "no args",
            ["get_revision_clouds"] = "revision_id?, limit?",
            ["get_sheets_by_revision"] = "revision_id",
            ["get_revision_schedule"] = "no args",

            // SelectionFilter (read-only)
            ["select_by_bounding_box"] = "min_x, min_y, max_x, max_y, category?, limit?",
            ["select_elements_in_view"] = "view_id?, category?, limit?",
            ["get_selection_summary"] = "no args",

            // ModelHealth (read-only)
            ["get_model_statistics"] = "no args",
            ["find_imported_cad"] = "limit?",
            ["find_inplace_families"] = "limit?",
            ["find_unused_families"] = "limit?",

            // PurgeAudit (read-only)
            ["get_purgeable_elements"] = "limit?",
            ["find_duplicate_types"] = "category?, limit?",
            ["find_unresolved_references"] = "limit?",
            ["get_design_options"] = "no args",
            ["audit_detail_levels"] = "limit?",

            // CoordinationReport (read-only)
            ["generate_clash_report"] = "category_a, category_b, limit?",
            ["compare_element_counts"] = "link_name?",
            ["get_link_coordination_status"] = "no args",
            ["get_scope_box_summary"] = "no args",

            // RevitLink (read-only)
            ["get_linked_models"] = "no args",
            ["get_linked_elements"] = "link_name, category?, limit?",
            ["count_linked_elements"] = "link_name, category?",
            ["get_linked_element_parameters"] = "link_name, element_id",
            ["search_linked_elements"] = "link_name, category, param_name, param_value",
            ["get_link_types"] = "link_name",

            // ClashDetection (read-only)
            ["find_overlapping"] = "category?, limit?",
            ["get_clash_summary"] = "category_a?, category_b?",

            // FamilyPlacement (read-only)
            ["get_family_types"] = "family_name?, category?",

            // Material (read-only)
            ["get_materials"] = "limit?",
            ["get_element_material"] = "element_ids (array)",
            ["get_material_quantities"] = "category, limit?",

            // SharedParameter (read-only)
            ["get_shared_parameters"] = "limit?",
            ["get_project_parameters"] = "limit?",
            ["check_parameter_values"] = "category, param_name, expected_value?, limit?",
            ["get_parameter_bindings"] = "param_name",

            // NamingAudit (read-only)
            ["audit_view_names"] = "pattern?, limit?",
            ["audit_sheet_numbers"] = "pattern?, limit?",
            ["audit_level_names"] = "pattern?, limit?",
            ["audit_family_names"] = "pattern?, limit?",
            ["audit_workset_names"] = "pattern?, limit?"
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
                "User: set transparency of elements 100, 200 to 50%\nAssistant:\n<tool_call>\n{\"name\": \"set_element_transparency\", \"arguments\": {\"element_ids\": [100, 200], \"transparency\": 50}}\n</tool_call>"),

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
                "User: export all walls to CSV\nAssistant:\n<tool_call>\n{\"name\": \"export_to_csv\", \"arguments\": {\"category\": \"Walls\", \"param_names\": [\"Mark\", \"Type Name\", \"Length\"], \"file_path\": \"C:\\\\temp\\\\walls.csv\"}}\n</tool_call>"),

            // Modify
            (new[] { "rename", "đổi tên" },
                "User: rename element 999 to 'Final'\nAssistant:\n<tool_call>\n{\"name\": \"rename_elements\", \"arguments\": {\"element_ids\": [999], \"new_name\": \"Final\"}}\n</tool_call>"),
            (new[] { "delete", "xóa" },
                "User: delete elements 111, 222, 333\nAssistant:\n<tool_call>\n{\"name\": \"delete_elements\", \"arguments\": {\"element_ids\": [111, 222, 333]}}\n</tool_call>"),
            (new[] { "set", "đặt", "parameter", "tham số", "value", "giá trị" },
                "User: set Mark parameter of element 999 to \"ABC\"\nAssistant:\n<tool_call>\n{\"name\": \"set_parameter_value\", \"arguments\": {\"element_ids\": [999], \"param_name\": \"Mark\", \"value\": \"ABC\"}}\n</tool_call>"),
            (new[] { "copy", "sao chép" },
                "User: copy elements 100, 200 by offset (3, 0, 0) feet\nAssistant:\n<tool_call>\n{\"name\": \"copy_elements\", \"arguments\": {\"element_ids\": [100, 200], \"offset_x\": 3.0, \"offset_y\": 0, \"offset_z\": 0}}\n</tool_call>"),
            (new[] { "move", "di chuyển" },
                "User: move element 300 by offset (2, 0, 0) feet\nAssistant:\n<tool_call>\n{\"name\": \"move_elements\", \"arguments\": {\"element_ids\": [300], \"offset_x\": 2.0, \"offset_y\": 0, \"offset_z\": 0}}\n</tool_call>"),

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
                "User: place a door\nAssistant:\nI'll first get the available door family types.\n<tool_call>\n{\"name\": \"get_family_types\", \"arguments\": {\"category\": \"Doors\"}}\n</tool_call>"),

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
            (new[] { "select", "chọn", "element" },
                "User: select elements 100, 200, 300\nAssistant:\n<tool_call>\n{\"name\": \"select_elements\", \"arguments\": {\"element_ids\": [100, 200, 300]}}\n</tool_call>"),
            (new[] { "select", "chọn", "all", "tất cả" },
                "User: select all walls on Level 1\nAssistant:\nI'll first get the wall elements on Level 1.\n<tool_call>\n{\"name\": \"get_elements\", \"arguments\": {\"category\": \"Walls\", \"level\": \"Level 1\"}}\n</tool_call>"),
            (new[] { "zoom", "phóng to", "focus" },
                "User: zoom to elements 100, 200\nAssistant:\n<tool_call>\n{\"name\": \"zoom_to_elements\", \"arguments\": {\"element_ids\": [100, 200]}}\n</tool_call>"),

            // Multi-step: count then export, query then modify
            (new[] { "count", "export", "csv" },
                "User: count all ducts then export to CSV\nAssistant:\nI'll count the ducts first.\n<tool_call>\n{\"name\": \"count_elements\", \"arguments\": {\"category\": \"Ducts\"}}\n</tool_call>"),
            (new[] { "list", "export", "csv" },
                "User: list all walls and export to CSV\nAssistant:\nI'll start by getting the wall data.\n<tool_call>\n{\"name\": \"get_elements\", \"arguments\": {\"category\": \"Walls\"}}\n</tool_call>"),
            (new[] { "count", "color", "override" },
                "User: count all pipes then change them to blue\nAssistant:\nI'll count the pipes first.\n<tool_call>\n{\"name\": \"count_elements\", \"arguments\": {\"category\": \"Pipes\"}}\n</tool_call>"),
            (new[] { "get", "then", "select" },
                "User: find all doors on Level 1 and select them\nAssistant:\nI'll get the door elements first.\n<tool_call>\n{\"name\": \"get_elements\", \"arguments\": {\"category\": \"Doors\", \"level\": \"Level 1\"}}\n</tool_call>"),

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

            // MEP detailed
            (new[] { "duct", "summary", "tóm tắt" },
                "User: summarize all ducts\nAssistant:\n<tool_call>\n{\"name\": \"get_duct_summary\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "pipe", "summary", "tóm tắt" },
                "User: summarize all pipes\nAssistant:\n<tool_call>\n{\"name\": \"get_pipe_summary\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "connector", "kết nối", "đầu nối" },
                "User: kiểm tra connector của element 12345\nAssistant:\n<tool_call>\n{\"name\": \"get_connector_info\", \"arguments\": {\"element_id\": 12345}}\n</tool_call>"),
            (new[] { "trace", "path", "đường ống", "route" },
                "User: trace hệ thống từ element 12345\nAssistant:\n<tool_call>\n{\"name\": \"get_system_connectivity\", \"arguments\": {\"element_id\": 12345, \"max_depth\": 30}}\n</tool_call>"),
            (new[] { "slope", "độ dốc" },
                "User: kiểm tra độ dốc ống thoát nước tầng 1 (min 1%)\nAssistant:\n<tool_call>\n{\"name\": \"check_pipe_slope\", \"arguments\": {\"system_name\": \"Drainage\", \"level\": \"Level 1\", \"min_slope_pct\": 1.0}}\n</tool_call>"),
            (new[] { "resize", "size", "đổi size" },
                "User: đổi size duct 400x200 thành 500x250 cho elements 100, 200\nAssistant:\n<tool_call>\n{\"name\": \"resize_mep_elements\", \"arguments\": {\"element_ids\": [100, 200], \"width_mm\": 500, \"height_mm\": 250}}\n</tool_call>"),
            (new[] { "preview", "dry run", "xem trước" },
                "User: preview resize ducts 100,200 to 500x250\nAssistant:\n<tool_call>\n{\"name\": \"resize_mep_elements\", \"arguments\": {\"element_ids\": [100, 200], \"width_mm\": 500, \"height_mm\": 250, \"dry_run\": true}}\n</tool_call>"),
            (new[] { "mechanical", "equipment", "thiết bị cơ", "FCU", "AHU" },
                "User: list all mechanical equipment\nAssistant:\n<tool_call>\n{\"name\": \"get_mechanical_equipment\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "plumbing", "fixture", "vệ sinh" },
                "User: list all plumbing fixtures\nAssistant:\n<tool_call>\n{\"name\": \"get_plumbing_fixtures\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "electrical", "equipment", "điện" },
                "User: list all electrical equipment\nAssistant:\n<tool_call>\n{\"name\": \"get_electrical_equipment\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "space", "airflow", "lưu lượng" },
                "User: check airflow balance for all spaces\nAssistant:\n<tool_call>\n{\"name\": \"check_space_airflow\", \"arguments\": {}}\n</tool_call>"),

            // Sheet management
            (new[] { "create", "sheet", "tạo", "bản vẽ" },
                "User: create sheet A101 named Floor Plan\nAssistant:\n<tool_call>\n{\"name\": \"create_sheet\", \"arguments\": {\"sheet_number\": \"A101\", \"sheet_name\": \"Floor Plan\"}}\n</tool_call>"),
            (new[] { "schedule", "bảng", "tạo schedule" },
                "User: tạo schedule cho Ducts gồm System Name, Size, Length, Level\nAssistant:\n<tool_call>\n{\"name\": \"create_schedule\", \"arguments\": {\"category\": \"Ducts\", \"field_names\": [\"System Name\", \"Size\", \"Length\", \"Level\"]}}\n</tool_call>"),
            (new[] { "schedule", "fields", "columns" },
                "User: list available schedule fields for Ducts\nAssistant:\n<tool_call>\n{\"name\": \"get_schedule_fields\", \"arguments\": {\"category\": \"Ducts\"}}\n</tool_call>"),
            (new[] { "trace", "connectivity", "path" },
                "User: trace connectivity from element 12345\nAssistant:\n<tool_call>\n{\"name\": \"get_system_connectivity\", \"arguments\": {\"element_id\": 12345, \"max_depth\": 30}}\n</tool_call>"),
            (new[] { "pipe", "slope" },
                "User: check pipe slopes in Drainage system (min 1%)\nAssistant:\n<tool_call>\n{\"name\": \"check_pipe_slope\", \"arguments\": {\"system_name\": \"Drainage\", \"min_slope_pct\": 1.0}}\n</tool_call>"),
            (new[] { "resize", "duct", "size" },
                "User: resize ducts 100,200 to 500x250\nAssistant:\n<tool_call>\n{\"name\": \"resize_mep_elements\", \"arguments\": {\"element_ids\": [100, 200], \"width_mm\": 500, \"height_mm\": 250}}\n</tool_call>"),

            // Selection filter
            (new[] { "select by", "chọn theo", "parameter" },
                "User: select all walls where Mark contains ABC\nAssistant:\n<tool_call>\n{\"name\": \"select_by_parameter_value\", \"arguments\": {\"category\": \"Walls\", \"parameter_name\": \"Mark\", \"value\": \"ABC\", \"match_type\": \"contains\"}}\n</tool_call>"),
            (new[] { "select", "in view", "trong view" },
                "User: select all ducts in current view\nAssistant:\n<tool_call>\n{\"name\": \"select_elements_in_view\", \"arguments\": {\"category\": \"Ducts\"}}\n</tool_call>"),

            // Room / Area detailed
            (new[] { "room", "boundary", "ranh giới" },
                "User: show boundaries of room 12345\nAssistant:\n<tool_call>\n{\"name\": \"get_room_boundaries\", \"arguments\": {\"room_id\": 12345}}\n</tool_call>"),
            (new[] { "room", "unplaced", "chưa đặt" },
                "User: find unplaced rooms\nAssistant:\n<tool_call>\n{\"name\": \"get_unplaced_rooms\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "area", "scheme", "diện tích" },
                "User: list area schemes\nAssistant:\n<tool_call>\n{\"name\": \"get_area_schemes\", \"arguments\": {}}\n</tool_call>"),

            // Clash / Clearance
            (new[] { "clearance", "khoảng cách" },
                "User: check clearance between ducts and pipes (min 0.5 feet)\nAssistant:\n<tool_call>\n{\"name\": \"check_clearance\", \"arguments\": {\"category_a\": \"Ducts\", \"category_b\": \"Pipes\", \"min_distance_feet\": 0.5}}\n</tool_call>"),
            (new[] { "overlap", "chồng chéo" },
                "User: find overlapping ducts\nAssistant:\n<tool_call>\n{\"name\": \"find_overlapping\", \"arguments\": {\"category\": \"Ducts\"}}\n</tool_call>"),

            // Grid / Level creation
            (new[] { "create", "level", "tạo", "tầng" },
                "User: create a new level at elevation 4000mm named Level 5\nAssistant:\n<tool_call>\n{\"name\": \"create_level\", \"arguments\": {\"name\": \"Level 5\", \"elevation\": 4000, \"unit\": \"mm\"}}\n</tool_call>"),
            (new[] { "rename", "level" },
                "User: rename level 12345 to Ground Floor\nAssistant:\n<tool_call>\n{\"name\": \"rename_level\", \"arguments\": {\"level_id\": 12345, \"new_name\": \"Ground Floor\"}}\n</tool_call>"),

            // Naming audit
            (new[] { "audit", "naming", "tên" },
                "User: audit view names\nAssistant:\n<tool_call>\n{\"name\": \"audit_view_names\", \"arguments\": {}}\n</tool_call>"),

            // Linked models detailed
            (new[] { "linked", "elements", "liên kết" },
                "User: count elements in linked models\nAssistant:\n<tool_call>\n{\"name\": \"count_linked_elements\", \"arguments\": {}}\n</tool_call>"),

            // Conduit / Cable tray
            (new[] { "conduit", "ống dẫn" },
                "User: summarize all conduits\nAssistant:\n<tool_call>\n{\"name\": \"get_conduit_summary\", \"arguments\": {}}\n</tool_call>"),
            (new[] { "cable tray", "khay cáp" },
                "User: summarize all cable trays\nAssistant:\n<tool_call>\n{\"name\": \"get_cable_tray_summary\", \"arguments\": {}}\n</tool_call>"),
        };

        private const int DefaultFewShotExamples = 5;
        private const int ComplexFewShotExamples = 7;

        private string BuildDynamicExamples(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return "";

            var lower = userMessage.ToLowerInvariant();
            var normalized = NormalizeForMatching(userMessage);
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

            if (scored.Count == 0) return "";

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
            return sb.ToString();
        }

        #endregion

        private static string NormalizeForMatching(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var text = input.ToLowerInvariant();
            foreach (var (from, to) in NormalizationMap)
                text = text.Replace(from, to);
            return text;
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
            var text = NormalizeForMatching(userMessage);
            int actionHits = 0;
            foreach (var kw in ActionKeywords)
            {
                if (MatchesKeyword(text, kw))
                    actionHits++;
            }

            if (actionHits >= 2) return true;

            var separators = new[] { " and ", " then ", " sau đó ", " rồi ", " và ", "&", "->" };
            return separators.Any(s => text.Contains(s));
        }

        private static bool MatchesKeyword(string text, string kw)
        {
            if (kw.Contains(' '))
                return text.Contains(kw);
            return Regex.IsMatch(text, $@"\b{Regex.Escape(kw)}\b");
        }

        private static List<(KeywordGroup group, int score)> GetMatchedGroups(string normalizedText)
        {
            var matches = new List<(KeywordGroup group, int score)>();
            foreach (var group in KeywordGroups)
            {
                int count = 0;
                foreach (var kw in group.Keywords)
                {
                    if (MatchesKeyword(normalizedText, kw))
                        count++;
                }
                if (count > 0)
                    matches.Add((group, count * group.Weight));
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

        private static readonly HashSet<string> ChitchatPatterns = new(StringComparer.OrdinalIgnoreCase)
        {
            "hello", "hi", "hey", "xin chào", "chào", "thanks", "thank you", "cảm ơn",
            "bye", "goodbye", "tạm biệt", "good morning", "good afternoon", "good evening",
            "what can you do", "help", "bạn là ai", "who are you", "giúp gì",
            "bạn có thể làm gì", "what are your capabilities"
        };

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
                    "If they ask what you can do, list: query elements, count, color override, isolate, export CSV, " +
                    "check MEP systems, BOQ, clash detection, modify parameters, and more. " +
                    "Reply in the same language the user uses. Do NOT output any <tool_call> tags.")
            };
            messages.AddRange(_conversationHistory);

            var response = await _client.CompleteChatAsync(messages, options, ct);
            var text = StripQwenTokens(response.Value.Content?.FirstOrDefault()?.Text ?? "");
            text = RemoveToolCallTags(text).Trim();
            _conversationHistory.Add(new AssistantChatMessage(text));
            return (text, new List<RevitChat.Models.ToolCallRequest>());
        }

        private bool ShouldUseTwoStage(string userMessage)
        {
            return ShouldUseTwoStageInternal(userMessage);
        }

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

            return await GetCompletionAsync(ct);
        }

        public async Task<(string assistantMessage, List<RevitChat.Models.ToolCallRequest> toolCalls)> ContinueWithToolResultsAsync(
            Dictionary<string, string> toolResults, CancellationToken ct = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"The user asked: \"{_lastUserMessage}\"");
            sb.AppendLine();
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

            sb.AppendLine("Analyze the results and answer the user's original question.");
            sb.AppendLine("If the user's request has more steps remaining (e.g. they asked to count THEN export), output ONE <tool_call> for the next step.");
            sb.AppendLine("If all steps are done, respond directly with NO <tool_call> tags.");

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

                if (_toolMode == "smart" && ShouldUseTwoStage(_lastUserMessage))
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

            var response = await _client.CompleteChatAsync(messages, options, ct);
            var text = StripQwenTokens(response.Value.Content?.FirstOrDefault()?.Text ?? "");

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
            var stage1Response = await _client.CompleteChatAsync(stage1Messages, options, ct);
            var stage1Text = StripQwenTokens(stage1Response.Value.Content?.FirstOrDefault()?.Text ?? "");

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

                var stage2Response = await _client.CompleteChatAsync(stage2Messages, options, ct);
                var stage2Text = StripQwenTokens(stage2Response.Value.Content?.FirstOrDefault()?.Text ?? "");

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
                // Smart mode: CoreTools + keyword match
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
            }

            var sb = new StringBuilder();
            foreach (var toolName in selected)
            {
                if (toolIndex.TryGetValue(toolName, out var tool))
                {
                    if (ToolSchemaHints.TryGetValue(tool.FunctionName, out var hint))
                        sb.AppendLine($"- {tool.FunctionName}: {tool.FunctionDescription} | args: {hint}");
                    else
                        sb.AppendLine($"- {tool.FunctionName}: {tool.FunctionDescription}");
                }
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
                _conversationHistory.Add(new AssistantChatMessage(
                    !string.IsNullOrEmpty(cleanText) ? cleanText : "(tool call)"));
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

            // 1) Well-formed <tool_call>...</tool_call>
            var tagPattern = new Regex(
                @"<tool_call>\s*(\{.+?\})\s*</tool_call>",
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
                var jsonObjects = Regex.Matches(inner, @"\{[^{}]*""name""[^{}]*\}", RegexOptions.Singleline);
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

            // 5) Last resort: find any known tool name followed by JSON-like args in garbled text
            foreach (var toolName in _allToolNames)
            {
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

            foreach (var tool in _allToolNames)
            {
                if (tool.Contains(lower) || lower.Contains(tool))
                    return tool;
            }

            var parts = lower.Split('_');
            string best = null;
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

        private string BuildFallbackSuggestion(string userMessage)
        {
            var normalized = NormalizeForMatching(userMessage);
            var matches = GetMatchedGroups(normalized)
                .OrderByDescending(m => m.score)
                .Take(2)
                .SelectMany(m => m.group.Tools)
                .Distinct()
                .Take(6)
                .ToList();

            var suggestion = matches.Count > 0
                ? string.Join(", ", matches)
                : "get_elements, count_elements, search_elements, get_levels, get_current_view";

            if (IsVietnamese(userMessage))
            {
                return "Mình chưa chọn được tool phù hợp từ yêu cầu này. " +
                       $"Bạn có thể nói rõ hơn (category/level/view) hoặc thử một trong các tool: {suggestion}.";
            }

            return "I couldn't determine the right tool for this request. " +
                   $"Please add more detail (category/level/view) or try one of these tools: {suggestion}.";
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
2. Output ONLY ONE <tool_call> per response. Stop writing IMMEDIATELY after </tool_call>.
3. Do NOT wrap <tool_call> in ```json``` code blocks.
4. The ""arguments"" field MUST be a JSON object with the tool's parameters inside it.
5. After receiving tool results, answer the user directly. If you need more data, output ONE more <tool_call>.
6. For multi-step requests (e.g. ""count ducts then export""), handle the FIRST step only. You will get results back and can do the next step then.
7. For destructive operations (delete, modify), confirm with the user FIRST before calling the tool.
8. NEVER invent data. Only use tool results.
9. Reply in the same language the user uses.
10. Vietnamese category mapping: tường=Walls, cửa=Doors, cửa sổ=Windows, ống=Ducts/Pipes, phòng=Rooms, sàn=Floors, cột=Columns, dầm=Structural Framing, trần=Ceilings, mái=Roofs, cầu thang=Stairs, lan can=Railings, thiết bị vệ sinh=Plumbing Fixtures, khay cáp=Cable Trays, ống dẫn=Conduits, đèn=Lighting Fixtures.

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
                        if (msg is UserChatMessage)
                            sb.AppendLine($"- User asked about model data");
                        else if (msg is AssistantChatMessage am)
                        {
                            var text = am.Content?.FirstOrDefault()?.Text ?? "";
                            if (text.Length > 80) text = text[..80] + "...";
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
