using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitChat.Services
{
    public enum PromptIntent
    {
        Unknown,
        Query,
        Count,
        Check,
        Create,
        Modify,
        Delete,
        Export,
        Visual,
        Navigate,
        Analyze,
        Connect,
        Tag
    }

    public sealed class PromptContext
    {
        public PromptIntent PrimaryIntent { get; internal set; } = PromptIntent.Unknown;
        public PromptIntent SecondaryIntent { get; internal set; } = PromptIntent.Unknown;
        public string DetectedCategory { get; internal set; }
        public string DetectedSystem { get; internal set; }
        public string DetectedLevel { get; internal set; }
        public List<long> DetectedElementIds { get; internal set; } = new();
        public string DetectedParameter { get; internal set; }
        public List<string> SuggestedTools { get; internal set; } = new();
        public string ContextHint { get; internal set; } = "";
    }

    /// <summary>
    /// Lightweight prompt analysis pipeline: Intent Classification → Entity Extraction → Context Enrichment.
    /// No external dependencies. Runs once per user message before LLM call.
    /// </summary>
    public static class PromptAnalyzer
    {
        #region Intent Patterns (keyword → weight)

        private static readonly (PromptIntent intent, (string kw, int w)[] patterns)[] IntentPatterns =
        {
            (PromptIntent.Count, new[] {
                ("count",3), ("how many",3), ("bao nhieu",3), ("dem",2),
                ("so luong",2), ("bao nhiêu",3), ("đếm",3), ("số lượng",2)
            }),
            (PromptIntent.Analyze, new[] {
                ("summary",3), ("statistics",3), ("thong ke",3), ("thống kê",3),
                ("tom tat",3), ("tóm tắt",3), ("boq",4), ("khoi luong",3),
                ("khối lượng",3), ("bao gia",3), ("báo giá",3), ("tong hop",2),
                ("tổng hợp",2), ("boc khoi luong",3), ("quantity takeoff",3),
                ("phan loai",2), ("classify",2), ("analyze",3), ("phân tích",3),
                ("tong",2)
            }),
            (PromptIntent.Query, new[] {
                ("list",2), ("get",2), ("show",2), ("liet ke",2), ("liệt kê",2),
                ("xem",2), ("hien thi",2), ("hiển thị",2), ("danh sach",2),
                ("find",2), ("tim",2), ("tìm",2), ("search",2), ("where",2),
                ("o dau",2), ("ở đâu",2), ("nao",1), ("which",2)
            }),
            (PromptIntent.Check, new[] {
                ("check",3), ("verify",3), ("audit",3), ("kiem tra",3), ("kiểm tra",3),
                ("validate",3), ("danh gia",2), ("đánh giá",2), ("health",2),
                ("warning",2), ("canh bao",2), ("clash",3), ("va cham",3),
                ("disconnect",3), ("ngat ket noi",3), ("missing",2), ("thieu",2)
            }),
            (PromptIntent.Create, new[] {
                ("create",3), ("make",2), ("add",2), ("tao",2), ("tạo",3),
                ("them",2), ("thêm",2), ("generate",2)
            }),
            (PromptIntent.Modify, new[] {
                ("set",3), ("change",3), ("modify",3), ("update",3),
                ("doi",2), ("đổi",2), ("sua",2), ("sửa",2), ("chinh",2),
                ("resize",3), ("split",3), ("chia",2), ("rename",3), ("flip",3)
            }),
            (PromptIntent.Delete, new[] {
                ("delete",4), ("remove",3), ("xoa",3), ("xóa",4),
                ("purge",3), ("don dep",2), ("dọn dẹp",2)
            }),
            (PromptIntent.Export, new[] {
                ("export",4), ("csv",3), ("xlsx",3), ("xuat",3), ("xuất",4),
                ("download",2), ("save file",2)
            }),
            (PromptIntent.Visual, new[] {
                ("color",3), ("mau",2), ("màu",3), ("hide",3), ("ẩn",3),
                ("unhide",3), ("isolate",4), ("co lap",3), ("cô lập",4),
                ("override",3), ("to mau",3), ("tô màu",3), ("doi mau",3),
                ("transparency",3), ("trong suot",2)
            }),
            (PromptIntent.Navigate, new[] {
                ("zoom",3), ("select",3), ("chon",2), ("chọn",3),
                ("highlight",3), ("focus",2), ("phong to",2)
            }),
            (PromptIntent.Connect, new[] {
                ("connect",3), ("noi ong",3), ("nối",3), ("ngat",2), ("ngắt",3),
                ("tap connection",3), ("elbow",3), ("coupling",3), ("bloom",2), ("co noi",3)
            }),
            (PromptIntent.Tag, new[] {
                ("tag",3), ("annotation",3), ("ghi chu",2), ("ghi chú",3),
                ("untagged",3), ("chua tag",3)
            })
        };

        #endregion

        #region Entity Dictionaries

        private static readonly (string[] keywords, string canonical, string revitCat)[] CategoryMap =
        {
            (new[]{"duct","ducts","ong gio","ống gió"}, "duct", "Ducts"),
            (new[]{"pipe","pipes","ong nuoc","ống nước","ong thoat nuoc","ong cap nuoc"}, "pipe", "Pipes"),
            (new[]{"conduit","conduits","ong dan","ống dẫn"}, "conduit", "Conduits"),
            (new[]{"cable tray","cable trays","khay cap","khay cáp","mang cap","máng cáp"}, "cable_tray", "Cable Trays"),
            (new[]{"wall","walls","tuong","tường"}, "wall", "Walls"),
            (new[]{"floor","floors","san","sàn"}, "floor", "Floors"),
            (new[]{"door","doors","cua di","cửa đi"}, "door", "Doors"),
            (new[]{"window","windows","cua so","cửa sổ"}, "window", "Windows"),
            (new[]{"room","rooms","phong","phòng"}, "room", "Rooms"),
            (new[]{"column","columns","cot","cột"}, "column", "Columns"),
            (new[]{"beam","beams","dam","dầm","structural framing"}, "beam", "Structural Framing"),
            (new[]{"ceiling","ceilings","tran","trần"}, "ceiling", "Ceilings"),
            (new[]{"sprinkler","sprinklers","dau phun","đầu phun"}, "sprinkler", "Sprinklers"),
            (new[]{"air terminal","air terminals","mieng gio","miệng gió","diffuser","grille","louver"}, "air_terminal", "Air Terminals"),
            (new[]{"mechanical equipment","thiet bi co","thiết bị cơ","ahu","fcu","vav"}, "mech_equip", "Mechanical Equipment"),
            (new[]{"electrical equipment","thiet bi dien","thiết bị điện","mdb","tu dien","tủ điện"}, "elec_equip", "Electrical Equipment"),
            (new[]{"plumbing fixture","thiet bi ve sinh","thiết bị vệ sinh"}, "plumb_fix", "Plumbing Fixtures"),
            (new[]{"lighting fixture","den","đèn"}, "light_fix", "Lighting Fixtures"),
            (new[]{"stair","stairs","cau thang","cầu thang"}, "stair", "Stairs"),
            (new[]{"railing","railings","lan can"}, "railing", "Railings"),
            (new[]{"furniture","noi that","nội thất"}, "furniture", "Furniture"),
            (new[]{"duct fitting","duct fittings","phu kien ong gio"}, "duct_fitting", "Duct Fittings"),
            (new[]{"pipe fitting","pipe fittings","phu kien ong nuoc"}, "pipe_fitting", "Pipe Fittings"),
            (new[]{"duct accessory","duct accessories","phu kien gio"}, "duct_acc", "Duct Accessories"),
            (new[]{"pipe accessory","pipe accessories","phu kien nuoc"}, "pipe_acc", "Pipe Accessories"),
            (new[]{"fire protection","pccc","chua chay","chữa cháy"}, "fire", "Fire Protection"),
        };

        private static readonly (string[] keywords, string canonical)[] SystemMap =
        {
            (new[]{"supply air","cap gio","cấp gió"}, "Supply Air"),
            (new[]{"return air","hoi gio","hồi gió"}, "Return Air"),
            (new[]{"exhaust air","hut gio","hút gió","exhaust"}, "Exhaust Air"),
            (new[]{"fresh air","gio tuoi","gió tươi","outside air"}, "Fresh Air"),
            (new[]{"chilled water","nuoc lanh","nước lạnh"}, "Chilled Water"),
            (new[]{"hot water","nuoc nong","nước nóng"}, "Hot Water"),
            (new[]{"condenser water","nuoc giai nhiet","nước giải nhiệt"}, "Condenser Water"),
            (new[]{"sanitary","thoat nuoc","thoát nước","waste"}, "Sanitary"),
            (new[]{"storm","nuoc mua","nước mưa","rain water"}, "Storm"),
            (new[]{"domestic water","nuoc cap","nước cấp"}, "Domestic Water"),
            (new[]{"fire protection","pccc","chua chay","chữa cháy"}, "Fire Protection"),
            (new[]{"condensate","nuoc ngung","nước ngưng"}, "Condensate"),
            (new[]{"vent","thong hoi","thông hơi"}, "Vent"),
            (new[]{"mixed air","gio hoa","gió hòa"}, "Mixed Air"),
        };

        private static readonly (string[] keywords, string canonical)[] AbbrSystemMap =
        {
            (new[]{"sa"}, "Supply Air"),
            (new[]{"ea"}, "Exhaust Air"), (new[]{"fa","oa"}, "Fresh Air"),
            (new[]{"chw","chws","chwr"}, "Chilled Water"),
            (new[]{"hw","hws","hwr","dhw"}, "Hot Water"),
            (new[]{"cw","cwr","cws"}, "Condenser Water"),
            (new[]{"sw"}, "Sanitary"), (new[]{"sd"}, "Storm"),
            (new[]{"dw"}, "Domestic Water"), (new[]{"fp","fps"}, "Fire Protection"),
        };

        private static readonly (string[] keywords, string canonical)[] ParameterMap =
        {
            (new[]{"velocity","van toc","vận tốc","speed"}, "Velocity"),
            (new[]{"pressure","ap suat","áp suất"}, "Pressure"),
            (new[]{"flow","luu luong","lưu lượng"}, "Flow"),
            (new[]{"size","kich co","kích cỡ","diameter","duong kinh","đường kính"}, "Size"),
            (new[]{"length","chieu dai","chiều dài"}, "Length"),
            (new[]{"slope","do doc","độ dốc"}, "Slope"),
            (new[]{"insulation","bao on","bảo ôn","cach nhiet","cách nhiệt"}, "Insulation"),
            (new[]{"mark"}, "Mark"),
            (new[]{"comments"}, "Comments"),
            (new[]{"system name","he thong","hệ thống"}, "System Name"),
        };

        private static readonly Regex LevelRegex = new(
            @"(?:level|tang|tầng|lvl)\s*[-:]?\s*(\S+)|(?:^|\s)(B\d+|L\d+|RF)(?:\s|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ElementIdRegex = new(
            @"(?:element|id|phần tử|phan tu)\s*(?:ids?\s*)?[:=]?\s*([\d,\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        #endregion

        #region Tool Suggestion Rules

        private static readonly Dictionary<(PromptIntent, string), string[]> ToolRules = new()
        {
            // Analyze
            {(PromptIntent.Analyze, "duct"), new[]{"get_duct_summary","mep_quantity_takeoff","calculate_system_totals"}},
            {(PromptIntent.Analyze, "pipe"), new[]{"get_pipe_summary","mep_quantity_takeoff","calculate_system_totals"}},
            {(PromptIntent.Analyze, "conduit"), new[]{"get_conduit_summary","mep_quantity_takeoff"}},
            {(PromptIntent.Analyze, "cable_tray"), new[]{"get_cable_tray_summary","mep_quantity_takeoff"}},
            {(PromptIntent.Analyze, "mech_equip"), new[]{"get_mechanical_equipment"}},
            {(PromptIntent.Analyze, "elec_equip"), new[]{"get_electrical_equipment"}},
            {(PromptIntent.Analyze, "plumb_fix"), new[]{"get_plumbing_fixtures"}},
            {(PromptIntent.Analyze, "_system"), new[]{"calculate_system_totals","get_system_elements"}},
            {(PromptIntent.Analyze, "_default"), new[]{"calculate_system_totals","mep_quantity_takeoff"}},

            // Count
            {(PromptIntent.Count, "_default"), new[]{"count_elements"}},
            {(PromptIntent.Count, "duct"), new[]{"count_elements","mep_quantity_takeoff"}},
            {(PromptIntent.Count, "pipe"), new[]{"count_elements","mep_quantity_takeoff"}},

            // Query
            {(PromptIntent.Query, "_default"), new[]{"get_elements","search_elements"}},
            {(PromptIntent.Query, "mech_equip"), new[]{"get_mechanical_equipment","get_elements"}},
            {(PromptIntent.Query, "elec_equip"), new[]{"get_electrical_equipment","get_elements"}},
            {(PromptIntent.Query, "plumb_fix"), new[]{"get_plumbing_fixtures","get_elements"}},
            {(PromptIntent.Query, "room"), new[]{"get_rooms","get_rooms_detailed"}},
            {(PromptIntent.Query, "air_terminal"), new[]{"get_elements"}},
            {(PromptIntent.Query, "sprinkler"), new[]{"get_fire_protection_equipment","get_elements"}},
            {(PromptIntent.Query, "fire"), new[]{"get_fire_protection_equipment"}},

            // Check
            {(PromptIntent.Check, "_default"), new[]{"audit_model_standards","get_model_warnings"}},
            {(PromptIntent.Check, "_velocity"), new[]{"check_velocity"}},
            {(PromptIntent.Check, "_slope"), new[]{"check_pipe_slope"}},
            {(PromptIntent.Check, "_insulation"), new[]{"check_insulation_coverage"}},
            {(PromptIntent.Check, "_disconnect"), new[]{"check_disconnected_elements"}},
            {(PromptIntent.Check, "_clash"), new[]{"check_clashes","get_clash_summary"}},

            // Export
            {(PromptIntent.Export, "_default"), new[]{"export_to_csv"}},
            {(PromptIntent.Export, "duct"), new[]{"export_mep_boq","export_to_csv"}},
            {(PromptIntent.Export, "pipe"), new[]{"export_mep_boq","export_to_csv"}},

            // Visual
            {(PromptIntent.Visual, "_system"), new[]{"override_color_by_system"}},
            {(PromptIntent.Visual, "_default"), new[]{"override_category_color","hide_category","isolate_category"}},

            // Tag
            {(PromptIntent.Tag, "_default"), new[]{"get_untagged_elements","tag_all_in_view"}},

            // Connect
            {(PromptIntent.Connect, "_default"), new[]{"connect_mep_elements","check_disconnected_elements"}},

            // Navigate
            {(PromptIntent.Navigate, "_default"), new[]{"select_elements","zoom_to_elements"}},
        };

        #endregion

        public static PromptContext Analyze(string userMessage)
        {
            var ctx = new PromptContext();
            if (string.IsNullOrWhiteSpace(userMessage)) return ctx;

            var lower = userMessage.ToLowerInvariant();
            var stripped = StripDiacritics(lower);
            var spaced = stripped.Replace('_', ' ').Replace('-', ' ');
            spaced = CollapseSpaces(spaced);

            ClassifyIntent(stripped, spaced, ctx);
            ExtractCategory(stripped, spaced, ctx);
            ExtractSystem(stripped, spaced, ctx);
            ExtractLevel(userMessage, stripped, ctx);
            ExtractElementIds(userMessage, ctx);
            ExtractParameter(stripped, spaced, ctx);
            SuggestTools(ctx, spaced);
            ctx.ContextHint = FormatHint(ctx);

            return ctx;
        }

        #region 1) Intent Classification

        private static void ClassifyIntent(string stripped, string spaced, PromptContext ctx)
        {
            var scores = new Dictionary<PromptIntent, int>();
            foreach (var (intent, patterns) in IntentPatterns)
            {
                int score = 0;
                foreach (var (kw, w) in patterns)
                {
                    var kwNorm = StripDiacritics(kw.ToLowerInvariant());
                    if (kwNorm.Contains(' '))
                    {
                        if (spaced.Contains(kwNorm) || stripped.Contains(kwNorm))
                            score += w;
                    }
                    else
                    {
                        if (ContainsWord(spaced, kwNorm) || ContainsWord(stripped, kwNorm))
                            score += w;
                    }
                }
                if (score > 0) scores[intent] = score;
            }

            if (scores.Count == 0) return;

            var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
            ctx.PrimaryIntent = ranked[0].Key;
            if (ranked.Count > 1 && ranked[1].Value >= 2)
                ctx.SecondaryIntent = ranked[1].Key;
        }

        private static bool ContainsWord(string text, string word)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(word)) return false;
            int idx = 0;
            while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
                int end = idx + word.Length;
                bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (leftOk && rightOk) return true;
                idx++;
            }
            return false;
        }

        #endregion

        #region 2) Entity Extraction

        private static void ExtractCategory(string stripped, string spaced, PromptContext ctx)
        {
            int bestLen = 0;
            string bestCanonical = null;
            string bestRevit = null;

            foreach (var (keywords, canonical, revitCat) in CategoryMap)
            {
                foreach (var kw in keywords)
                {
                    if (kw.Length <= bestLen) continue;
                    var kwNorm = StripDiacritics(kw.ToLowerInvariant());
                    if (kwNorm.Contains(' '))
                    {
                        if (spaced.Contains(kwNorm) || stripped.Contains(kwNorm))
                        { bestLen = kw.Length; bestCanonical = canonical; bestRevit = revitCat; }
                    }
                    else
                    {
                        if (ContainsWord(spaced, kwNorm) || ContainsWord(stripped, kwNorm))
                        { bestLen = kw.Length; bestCanonical = canonical; bestRevit = revitCat; }
                    }
                }
            }

            if (bestRevit != null)
                ctx.DetectedCategory = bestRevit;
        }

        private static void ExtractSystem(string stripped, string spaced, PromptContext ctx)
        {
            int bestLen = 0;
            foreach (var (keywords, canonical) in SystemMap)
            {
                foreach (var kw in keywords)
                {
                    if (kw.Length <= bestLen) continue;
                    var kwNorm = StripDiacritics(kw.ToLowerInvariant());
                    if (kwNorm.Contains(' '))
                    {
                        if (spaced.Contains(kwNorm) || stripped.Contains(kwNorm))
                        { ctx.DetectedSystem = canonical; bestLen = kw.Length; }
                    }
                    else if (kw.Length > 2)
                    {
                        if (ContainsWord(spaced, kwNorm) || ContainsWord(stripped, kwNorm))
                        { ctx.DetectedSystem = canonical; bestLen = kw.Length; }
                    }
                }
            }

            if (ctx.DetectedSystem != null) return;

            foreach (var (keywords, canonical) in AbbrSystemMap)
            {
                foreach (var abbr in keywords)
                {
                    if (ContainsWord(stripped, abbr) || ContainsWord(spaced, abbr))
                    { ctx.DetectedSystem = canonical; return; }
                }
            }
        }

        private static void ExtractLevel(string original, string stripped, PromptContext ctx)
        {
            var match = LevelRegex.Match(original);
            if (!match.Success) match = LevelRegex.Match(stripped);
            if (match.Success)
            {
                var val = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                ctx.DetectedLevel = val.Trim();
            }
        }

        private static void ExtractElementIds(string original, PromptContext ctx)
        {
            var match = ElementIdRegex.Match(original);
            if (match.Success)
            {
                foreach (var part in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(part.Trim(), out var id) && id > 0)
                        ctx.DetectedElementIds.Add(id);
                }
            }
        }

        private static void ExtractParameter(string stripped, string spaced, PromptContext ctx)
        {
            foreach (var (keywords, canonical) in ParameterMap)
            {
                foreach (var kw in keywords)
                {
                    var kwNorm = StripDiacritics(kw.ToLowerInvariant());
                    if (spaced.Contains(kwNorm) || stripped.Contains(kwNorm))
                    { ctx.DetectedParameter = canonical; return; }
                }
            }
        }

        #endregion

        #region 3) Tool Suggestion

        private static void SuggestTools(PromptContext ctx, string normalizedPrompt)
        {
            if (ctx.PrimaryIntent == PromptIntent.Unknown) return;

            var catKey = GetCanonicalKey(ctx.DetectedCategory);

            if (ctx.PrimaryIntent == PromptIntent.Check)
            {
                string checkSubType = ctx.DetectedParameter switch
                {
                    "Velocity" => "_velocity",
                    "Slope" => "_slope",
                    "Insulation" => "_insulation",
                    _ => null
                };

                if (checkSubType == null)
                {
                    if (normalizedPrompt.Contains("clash") || normalizedPrompt.Contains("va cham")
                        || normalizedPrompt.Contains("xung dot"))
                        checkSubType = "_clash";
                    else if (normalizedPrompt.Contains("disconnect") || ContainsWord(normalizedPrompt, "ngat")
                             || normalizedPrompt.Contains("khong noi") || normalizedPrompt.Contains("bi ho"))
                        checkSubType = "_disconnect";
                }

                if (checkSubType != null)
                    catKey = checkSubType;
            }

            if (ctx.PrimaryIntent == PromptIntent.Visual && ctx.DetectedSystem != null)
                catKey = "_system";

            if (ctx.PrimaryIntent == PromptIntent.Analyze && ctx.DetectedSystem != null && catKey == null)
                catKey = "_system";

            var key = catKey != null ? (ctx.PrimaryIntent, catKey) : (ctx.PrimaryIntent, "_default");

            if (ToolRules.TryGetValue(key, out var tools))
                ctx.SuggestedTools = new List<string>(tools);
            else if (ToolRules.TryGetValue((ctx.PrimaryIntent, "_default"), out var defaults))
                ctx.SuggestedTools = new List<string>(defaults);
        }

        private static string GetCanonicalKey(string revitCategory)
        {
            if (string.IsNullOrEmpty(revitCategory)) return null;
            foreach (var (_, canonical, revitCat) in CategoryMap)
            {
                if (revitCat.Equals(revitCategory, StringComparison.OrdinalIgnoreCase))
                    return canonical;
            }
            return null;
        }

        #endregion

        #region 4) Context Hint Formatting

        private static string FormatHint(PromptContext ctx)
        {
            if (ctx.PrimaryIntent == PromptIntent.Unknown
                && ctx.DetectedCategory == null
                && ctx.DetectedSystem == null)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("[Context Analysis]");

            if (ctx.PrimaryIntent != PromptIntent.Unknown)
            {
                sb.Append($"- Intent: {FormatIntent(ctx.PrimaryIntent)}");
                if (ctx.SecondaryIntent != PromptIntent.Unknown)
                    sb.Append($" (secondary: {FormatIntent(ctx.SecondaryIntent)})");
                sb.AppendLine();
            }

            if (ctx.DetectedCategory != null)
                sb.AppendLine($"- Category: {ctx.DetectedCategory}");
            if (ctx.DetectedSystem != null)
                sb.AppendLine($"- System: {ctx.DetectedSystem}");
            if (ctx.DetectedLevel != null)
                sb.AppendLine($"- Level: {ctx.DetectedLevel}");
            if (ctx.DetectedParameter != null)
                sb.AppendLine($"- Parameter: {ctx.DetectedParameter}");
            if (ctx.DetectedElementIds.Count > 0)
                sb.AppendLine($"- Element IDs: {string.Join(", ", ctx.DetectedElementIds.Take(10))}");

            if (ctx.SuggestedTools.Count > 0)
                sb.AppendLine($"- Suggested tools: {string.Join(", ", ctx.SuggestedTools)}");

            return sb.ToString().TrimEnd();
        }

        private static string FormatIntent(PromptIntent intent) => intent switch
        {
            PromptIntent.Query => "query/list data",
            PromptIntent.Count => "count elements",
            PromptIntent.Check => "check/audit/validate",
            PromptIntent.Create => "create new elements",
            PromptIntent.Modify => "modify/update elements",
            PromptIntent.Delete => "delete/remove elements",
            PromptIntent.Export => "export data",
            PromptIntent.Visual => "visual override (color/hide/isolate)",
            PromptIntent.Navigate => "navigate/select elements",
            PromptIntent.Analyze => "analyze/summarize/BOQ",
            PromptIntent.Connect => "connect/disconnect MEP",
            PromptIntent.Tag => "tag/annotate elements",
            _ => intent.ToString()
        };

        #endregion

        #region Helpers

        private static string StripDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC)
                     .Replace('đ', 'd').Replace('Đ', 'D');
        }

        private static string CollapseSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            bool prev = false;
            foreach (var ch in s)
            {
                if (ch == ' ') { if (!prev) { sb.Append(' '); prev = true; } }
                else { sb.Append(ch); prev = false; }
            }
            return sb.ToString().Trim();
        }

        #endregion
    }
}
