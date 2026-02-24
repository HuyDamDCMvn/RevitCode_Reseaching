using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitChat.Skills
{
    /// <summary>
    /// Shared helper methods for Revit API operations used across skills.
    /// </summary>
    internal static class RevitHelpers
    {
        internal static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

        internal static string GetFamilyName(Document doc, Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol?.Family != null)
                return fi.Symbol.Family.Name;

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    var familyParam = elemType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (familyParam != null && familyParam.HasValue)
                        return familyParam.AsString();
                    return elemType.Name;
                }
            }
            return elem.GetType().Name;
        }

        internal static string GetElementTypeName(Document doc, Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol != null)
                return fi.Symbol.Name;

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null) return elemType.Name;
            }
            return elem.Name ?? "-";
        }

        internal static string GetElementLevel(Document doc, Element elem)
        {
            var levelParam = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                          ?? elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                          ?? elem.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                          ?? elem.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID);

            if (levelParam != null && levelParam.StorageType == StorageType.ElementId)
            {
                var lvl = doc.GetElement(levelParam.AsElementId()) as Level;
                if (lvl != null) return lvl.Name;
            }

            if (elem.LevelId != ElementId.InvalidElementId)
            {
                var lvl = doc.GetElement(elem.LevelId) as Level;
                if (lvl != null) return lvl.Name;
            }

            return "-";
        }

        /// <summary>
        /// Resolves a user-provided level string (e.g. "2", "Level 2", "L2", "tang 2")
        /// to the actual Revit level name. Returns null if no match found.
        /// </summary>
        internal static string ResolveLevelName(Document doc, string userLevel)
        {
            if (string.IsNullOrWhiteSpace(userLevel)) return null;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Select(l => l.Name)
                .ToList();

            var input = userLevel.Trim();

            // Exact match (case insensitive)
            var exact = levels.FirstOrDefault(l => l.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Extract numeric/alphanumeric suffix from input: "Level 2" → "2", "tang 3" → "3", "L2" → "2"
            var inputSuffix = ExtractLevelSuffix(input);
            if (!string.IsNullOrEmpty(inputSuffix))
            {
                // Match level where suffix matches: "L2" suffix is "2", input suffix "2" → match
                var suffixMatch = levels.FirstOrDefault(l =>
                    ExtractLevelSuffix(l).Equals(inputSuffix, StringComparison.OrdinalIgnoreCase));
                if (suffixMatch != null) return suffixMatch;

                // Partial: level name contains the input suffix as word
                var partialMatch = levels.FirstOrDefault(l =>
                    l.IndexOf(inputSuffix, StringComparison.OrdinalIgnoreCase) >= 0);
                if (partialMatch != null) return partialMatch;
            }

            // Input contained in level name or vice versa
            var containsMatch = levels.FirstOrDefault(l =>
                l.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0
                || input.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0);
            return containsMatch;
        }

        private static string ExtractLevelSuffix(string levelName)
        {
            if (string.IsNullOrEmpty(levelName)) return "";
            // Strip common prefixes: Level, L, tang, tầng, lvl, B(basement)
            var trimmed = System.Text.RegularExpressions.Regex.Replace(
                levelName.Trim(),
                @"^(?:level|lvl|tang|tầng|tâng|l(?=\d))\s*[-:]?\s*",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return trimmed.Trim();
        }

        internal static string GetParameterValueAsString(Document doc, Parameter param)
        {
            if (param == null || !param.HasValue) return "-";

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "-";
                case StorageType.Integer:
                    if (param.Definition?.GetDataType() == SpecTypeId.Boolean.YesNo)
                        return param.AsInteger() == 1 ? "Yes" : "No";
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F2");
                case StorageType.ElementId:
                    var elemId = param.AsElementId();
                    if (elemId == ElementId.InvalidElementId) return "-";
                    var refElem = doc.GetElement(elemId);
                    return refElem?.Name ?? elemId.Value.ToString();
                default:
                    return "-";
            }
        }

        private static readonly Dictionary<string, BuiltInCategory> CategoryAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["duct"] = BuiltInCategory.OST_DuctCurves,
            ["ducts"] = BuiltInCategory.OST_DuctCurves,
            ["duct curves"] = BuiltInCategory.OST_DuctCurves,
            ["ong gio"] = BuiltInCategory.OST_DuctCurves,
            ["pipe"] = BuiltInCategory.OST_PipeCurves,
            ["pipes"] = BuiltInCategory.OST_PipeCurves,
            ["pipe curves"] = BuiltInCategory.OST_PipeCurves,
            ["ong nuoc"] = BuiltInCategory.OST_PipeCurves,
            ["conduit"] = BuiltInCategory.OST_Conduit,
            ["conduits"] = BuiltInCategory.OST_Conduit,
            ["cable tray"] = BuiltInCategory.OST_CableTray,
            ["cable trays"] = BuiltInCategory.OST_CableTray,
            ["flex duct"] = BuiltInCategory.OST_FlexDuctCurves,
            ["flex ducts"] = BuiltInCategory.OST_FlexDuctCurves,
            ["flex pipe"] = BuiltInCategory.OST_FlexPipeCurves,
            ["flex pipes"] = BuiltInCategory.OST_FlexPipeCurves,
            ["duct fitting"] = BuiltInCategory.OST_DuctFitting,
            ["duct fittings"] = BuiltInCategory.OST_DuctFitting,
            ["pipe fitting"] = BuiltInCategory.OST_PipeFitting,
            ["pipe fittings"] = BuiltInCategory.OST_PipeFitting,
            ["duct accessory"] = BuiltInCategory.OST_DuctAccessory,
            ["duct accessories"] = BuiltInCategory.OST_DuctAccessory,
            ["pipe accessory"] = BuiltInCategory.OST_PipeAccessory,
            ["pipe accessories"] = BuiltInCategory.OST_PipeAccessory,
            ["mechanical equipment"] = BuiltInCategory.OST_MechanicalEquipment,
            ["electrical equipment"] = BuiltInCategory.OST_ElectricalEquipment,
            ["plumbing fixture"] = BuiltInCategory.OST_PlumbingFixtures,
            ["plumbing fixtures"] = BuiltInCategory.OST_PlumbingFixtures,
            ["sprinkler"] = BuiltInCategory.OST_Sprinklers,
            ["sprinklers"] = BuiltInCategory.OST_Sprinklers,
            ["dau phun"] = BuiltInCategory.OST_Sprinklers,
            ["air terminal"] = BuiltInCategory.OST_DuctTerminal,
            ["air terminals"] = BuiltInCategory.OST_DuctTerminal,
            ["diffuser"] = BuiltInCategory.OST_DuctTerminal,
            ["grille"] = BuiltInCategory.OST_DuctTerminal,
            ["louver"] = BuiltInCategory.OST_DuctTerminal,
            ["mang cap"] = BuiltInCategory.OST_CableTray,
            ["ong dan"] = BuiltInCategory.OST_Conduit,
            ["thiet bi co"] = BuiltInCategory.OST_MechanicalEquipment,
            ["thiet bi dien"] = BuiltInCategory.OST_ElectricalEquipment,
            ["thiet bi ve sinh"] = BuiltInCategory.OST_PlumbingFixtures,
            ["lighting fixture"] = BuiltInCategory.OST_LightingFixtures,
            ["lighting fixtures"] = BuiltInCategory.OST_LightingFixtures,
            ["den"] = BuiltInCategory.OST_LightingFixtures,
            ["conduit fitting"] = BuiltInCategory.OST_ConduitFitting,
            ["conduit fittings"] = BuiltInCategory.OST_ConduitFitting,
            ["cable tray fitting"] = BuiltInCategory.OST_CableTrayFitting,
            ["cable tray fittings"] = BuiltInCategory.OST_CableTrayFitting,
            ["electrical fixture"] = BuiltInCategory.OST_ElectricalFixtures,
            ["electrical fixtures"] = BuiltInCategory.OST_ElectricalFixtures,
            ["o cam"] = BuiltInCategory.OST_ElectricalFixtures,
            ["cong tac"] = BuiltInCategory.OST_ElectricalFixtures,
            ["wall"] = BuiltInCategory.OST_Walls,
            ["walls"] = BuiltInCategory.OST_Walls,
            ["floor"] = BuiltInCategory.OST_Floors,
            ["floors"] = BuiltInCategory.OST_Floors,
            ["door"] = BuiltInCategory.OST_Doors,
            ["doors"] = BuiltInCategory.OST_Doors,
            ["window"] = BuiltInCategory.OST_Windows,
            ["windows"] = BuiltInCategory.OST_Windows,
            ["room"] = BuiltInCategory.OST_Rooms,
            ["rooms"] = BuiltInCategory.OST_Rooms,
            ["column"] = BuiltInCategory.OST_Columns,
            ["columns"] = BuiltInCategory.OST_Columns,
            ["structural column"] = BuiltInCategory.OST_StructuralColumns,
            ["structural columns"] = BuiltInCategory.OST_StructuralColumns,
            ["structural framing"] = BuiltInCategory.OST_StructuralFraming,
            ["beam"] = BuiltInCategory.OST_StructuralFraming,
            ["beams"] = BuiltInCategory.OST_StructuralFraming,
            ["ceiling"] = BuiltInCategory.OST_Ceilings,
            ["ceilings"] = BuiltInCategory.OST_Ceilings,
            ["roof"] = BuiltInCategory.OST_Roofs,
            ["roofs"] = BuiltInCategory.OST_Roofs,
            ["stair"] = BuiltInCategory.OST_Stairs,
            ["stairs"] = BuiltInCategory.OST_Stairs,
            ["railing"] = BuiltInCategory.OST_StairsRailing,
            ["railings"] = BuiltInCategory.OST_StairsRailing,
            ["furniture"] = BuiltInCategory.OST_Furniture,
            ["noi that"] = BuiltInCategory.OST_Furniture,
            ["generic model"] = BuiltInCategory.OST_GenericModel,
            ["generic models"] = BuiltInCategory.OST_GenericModel,
            ["tuong"] = BuiltInCategory.OST_Walls,
            ["san"] = BuiltInCategory.OST_Floors,
            ["cua"] = BuiltInCategory.OST_Doors,
            ["cua so"] = BuiltInCategory.OST_Windows,
            ["phong"] = BuiltInCategory.OST_Rooms,
            ["cot"] = BuiltInCategory.OST_Columns,
            ["dam"] = BuiltInCategory.OST_StructuralFraming,
            ["tran"] = BuiltInCategory.OST_Ceilings,
            ["mai"] = BuiltInCategory.OST_Roofs,
            ["cau thang"] = BuiltInCategory.OST_Stairs,
            ["lan can"] = BuiltInCategory.OST_StairsRailing
        };

        // ──────────────────────────────────────────────────────────
        //  Common abbreviations for MEP systems (expanded on demand)
        // ──────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── HVAC / Duct systems ──
            ["SA"]   = "supply air",    ["RA"]   = "return air",    ["EA"]   = "exhaust air",
            ["OA"]   = "outside air",   ["FA"]   = "fresh air",     ["MA"]   = "mixed air",
            ["AHU"]  = "air handling unit",   ["FCU"]  = "fan coil unit",
            ["VAV"]  = "variable air volume", ["CAV"]  = "constant air volume",
            ["RTU"]  = "rooftop unit",        ["MAU"]  = "makeup air unit",
            ["PAU"]  = "primary air unit",    ["ACU"]  = "air conditioning unit",
            ["ERV"]  = "energy recovery ventilator", ["HRV"] = "heat recovery ventilator",
            ["HRU"]  = "heat recovery unit",  ["FFU"]  = "fan filter unit",
            ["VRF"]  = "variable refrigerant flow",  ["VRV"] = "variable refrigerant volume",
            ["DOAS"] = "dedicated outdoor air system",
            ["CRAC"] = "computer room air conditioning",
            ["DX"]   = "direct expansion",    ["HP"]  = "heat pump",
            // ── HVAC Abbreviations from drawings (229-term standard) ──
            ["EAF"]  = "exhaust air fan",     ["FAF"]  = "fresh air fan",
            ["RAF"]  = "return air fan",      ["SEF"]  = "smoke exhaust fan",
            ["SPF"]  = "staircase pressurization fan", ["LPF"] = "lift pressurization fan",
            ["TEF"]  = "toilet exhaust fan",  ["KEF"]  = "kitchen exhaust fan",
            ["FD"]   = "fire damper",         ["FSD"]  = "fire smoke damper",
            ["MD"]   = "motorized damper",    ["VCD"]  = "volume control damper",
            ["BDD"]  = "backdraft damper",    ["NRD"]  = "non return damper",
            ["VFD"]  = "variable frequency drive", ["VSD"] = "variable speed drive",
            ["DDC"]  = "direct digital control",
            ["BAS"]  = "building automation system", ["BMS"] = "building management system",
            ["FAS"]  = "fire alarm system",
            ["ATT"]  = "attenuator",          ["DN"]  = "nominal diameter",
            ["ESP"]  = "external static pressure",   ["SP"] = "static pressure",
            ["DP"]   = "differential pressure",
            ["ET"]   = "expansion tank",      ["PHE"]  = "plate heat exchanger",
            ["HRC"]  = "heat recovery chiller",
            ["BOD"]  = "bottom of duct",      ["BOP"]  = "bottom of pipe",
            ["COP"]  = "center of pipe",      ["FFL"]  = "finished floor level",
            ["CDP"]  = "condensate drain piping",
            // ── Piping / Plumbing systems ──
            ["HW"]   = "hot water",     ["CHW"]  = "chilled water", ["CW"]  = "cold water",
            ["DW"]   = "domestic water", ["RW"]   = "rain water",   ["WW"]  = "waste water",
            ["DHW"]  = "domestic hot water",
            ["HWS"]  = "hot water supply",   ["HWR"] = "hot water return",
            ["CHWS"] = "chilled water supply",["CHWR"]= "chilled water return",
            ["CWS"]  = "cold water supply",  ["CWR"] = "cold water return",
            ["CHWP"] = "chilled water pump",  ["CWP"] = "condenser water pump",
            ["PCHWP"]= "primary chilled water pump",
            ["SW"]   = "sanitary waste",     ["SV"]  = "sanitary vent",
            ["SD"]   = "storm drain",        ["RD"]  = "roof drain",
            ["PRV"]  = "pressure reducing valve", ["TMV"] = "thermostatic mixing valve",
            ["SOV"]  = "shut off valve",     ["IV"]  = "isolating valve",
            ["CV"]   = "check valve",        ["BV"]  = "butterfly valve",
            ["GV"]   = "gate valve",         ["RV"]  = "relief valve",
            ["PICV"] = "pressure independent control valve",
            ["GPM"]  = "gallons per minute", ["LPS"] = "liters per second",
            // ── Fire Protection ──
            ["FP"]   = "fire protection",    ["FPS"]  = "fire protection sprinkler",
            ["FHC"]  = "fire hose cabinet",  ["FDC"]  = "fire department connection",
            // ── Electrical ──
            ["CT"]   = "cable tray",    ["CD"]   = "conduit",
            ["MDB"]  = "main distribution board",  ["SMDB"] = "sub main distribution board",
            ["DB"]   = "distribution board",        ["MCC"]  = "motor control center",
            ["UPS"]  = "uninterruptible power supply", ["ATS"] = "automatic transfer switch",
            ["CB"]   = "circuit breaker",    ["AWG"]  = "american wire gauge",
            // ── Equipment ──
            ["ME"]   = "mechanical equipment", ["EE"]  = "electrical equipment",
            ["PF"]   = "plumbing fixture",
            // ── Revit System Classifications (exact enum names) ──
            ["supply hydronic"]   = "supply hydronic",
            ["return hydronic"]   = "return hydronic",
            ["domestic hot water"]= "domestic hot water",
            ["domestic cold water"]="domestic cold water",
            ["fire protect wet"]  = "fire protection wet",
            ["fire protect dry"]  = "fire protection dry",
            ["condensate drain"]  = "condensate drain",
            // ── Vietnamese (no-diacritics) → English ──
            ["cap gio"]     = "supply air",   ["hoi gio"]    = "return air",
            ["hut gio"]     = "exhaust air",  ["gio tuoi"]   = "fresh air",
            ["nuoc nong"]   = "hot water",    ["nuoc lanh"]  = "chilled water",
            ["nuoc thai"]   = "waste water",  ["nuoc mua"]   = "rain water",
            ["nuoc cap"]    = "water supply", ["nuoc thoat"]  = "drainage",
            ["gio hoa"]     = "mixed air",    ["gio ngoai"]   = "outside air",
            ["pccc"]        = "fire protection", ["chua chay"] = "fire protection",
            ["ong gio"]     = "duct",         ["ong nuoc"]   = "pipe",
            ["ong dan"]     = "conduit",      ["mang cap"]   = "cable tray",
            ["phu kien"]    = "fitting",      ["co noi"]     = "elbow",
            ["dau phun"]    = "sprinkler",    ["bom"]        = "pump",
            ["quat"]        = "fan",          ["van"]        = "valve",
            ["may lanh"]    = "chiller",      ["noi hoi"]    = "boiler",
            ["dieu hoa"]    = "air conditioning",
            ["thiet bi co"] = "mechanical equipment",
            ["thiet bi dien"]  = "electrical equipment",
            ["thiet bi ve sinh"]= "plumbing fixture",
            ["tu dien"]     = "distribution board",
            ["thap giai nhiet"]= "cooling tower",
            ["binh gian no"]= "expansion tank",
            ["binh tich ap"]= "pressure tank",
            ["gia do"]      = "hanger",       ["bao on"]     = "insulation",
            ["cach nhiet"]  = "insulation",   ["thong gio"]  = "ventilation",
            ["cap nuoc"]    = "water supply", ["thoat nuoc"] = "drainage",
            // ── Vietnamese construction slang / informal ──
            ["mieng gio"]   = "air terminal",  ["cua gio"]    = "damper",
            ["dan lanh"]    = "fan coil unit",  ["dan nong"]   = "condensing unit",
            ["ong gas"]     = "refrigerant pipe",["ong mem"]   = "flex duct",
            ["quat hut"]    = "exhaust fan",    ["quat cap"]   = "supply fan",
            ["quat hut khoi"]= "smoke exhaust fan",
            ["quat tao ap"] = "pressurization fan",
            ["quat thai"]   = "exhaust fan",    ["quat tuoi"]  = "fresh air fan",
            ["van buom"]    = "butterfly valve", ["van cong"]   = "gate valve",
            ["van 1 chieu"] = "check valve",    ["van chan"]    = "isolating valve",
            ["van ngan chay"]= "fire damper",   ["van khoi"]   = "smoke damper",
            ["van gio"]     = "volume control damper",
            ["bom tang ap"] = "booster pump",
            ["bom nuoc lanh"]= "chilled water pump",
            ["bom giai nhiet"]= "condenser water pump",
            ["nuoc ngung"]  = "condensate",     ["nuoc giai nhiet"]= "condenser water",
            ["bien tan"]    = "variable frequency drive",
            ["cam bien"]    = "sensor",         ["cam bien nhiet"]= "temperature sensor",
            ["cam bien ap"]  = "pressure sensor",
            ["bo tieu am"]  = "attenuator",     ["giam on"]    = "attenuator",
            ["he thong bao chay"]= "fire alarm system",
            ["phong sach"]  = "clean room",     ["phong may"]  = "mechanical room",
            ["ong ngung"]   = "condensate drain piping",
            ["ong thoat nuoc ngung"]= "condensate drain piping",
        };

        // ──────────────────────────────────────────────────────────
        //  NormalizeArg: single source of truth for string cleanup
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Normalize a tool argument string: trim, replace _/- with space,
        /// collapse multiple spaces, strip diacritics.
        /// </summary>
        internal static string NormalizeArg(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var s = input.Trim();
            s = s.Replace('_', ' ').Replace('-', ' ');
            s = StripDiacritics(s);
            s = CollapseSpaces(s);
            return s;
        }

        private static string StripDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                    != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC)
                     .Replace('đ', 'd').Replace('Đ', 'D');
        }

        private static string CollapseSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            bool prev = false;
            foreach (var ch in s)
            {
                if (ch == ' ') { if (!prev) { sb.Append(' '); prev = true; } }
                else { sb.Append(ch); prev = false; }
            }
            return sb.ToString().Trim();
        }

        private static string StripAllSpaces(string s) => s?.Replace(" ", "") ?? "";

        private static string StripPlural(string s)
        {
            if (s.Length <= 2) return s;
            if (s.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
                return s[..^3] + "y";
            if (s.EndsWith("ses", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith("xes", StringComparison.OrdinalIgnoreCase))
                return s[..^2];
            if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                !s.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
                return s[..^1];
            return s;
        }

        /// <summary>
        /// Expand abbreviations (e.g. "SA" → "supply air") if found.
        /// Also handles no-diacritics Vietnamese shortcuts.
        /// </summary>
        private static string ExpandAbbreviations(string norm)
        {
            if (Abbreviations.TryGetValue(norm, out var expanded))
                return expanded;
            return norm;
        }

        /// <summary>
        /// Split into lowercase tokens for word-level matching.
        /// </summary>
        private static string[] Tokenize(string norm)
            => norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>
        /// Check if ALL tokens from <paramref name="filterTokens"/> appear
        /// somewhere in <paramref name="actualTokens"/> (order-independent).
        /// Handles partial token matches (filter token is prefix of actual token).
        /// </summary>
        private static bool AllTokensPresent(string[] filterTokens, string[] actualTokens)
        {
            foreach (var ft in filterTokens)
            {
                bool found = false;
                foreach (var at in actualTokens)
                {
                    if (at.Contains(ft, StringComparison.OrdinalIgnoreCase) ||
                        ft.Contains(at, StringComparison.OrdinalIgnoreCase))
                    { found = true; break; }
                }
                if (!found) return false;
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────
        //  FuzzyContains — the core engine used by all matchers
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Multi-strategy fuzzy string match. Returns true if <paramref name="filter"/>
        /// matches <paramref name="actual"/> through any of these strategies:
        /// <list type="number">
        ///   <item>Direct Contains (case-insensitive)</item>
        ///   <item>Normalized Contains (diacritics, underscores, hyphens stripped)</item>
        ///   <item>Plural/singular tolerance</item>
        ///   <item>No-space collapsed match ("supplyair" ↔ "supply air")</item>
        ///   <item>Abbreviation expansion ("SA" → "supply air")</item>
        ///   <item>Token overlap — all filter words found in actual, any order</item>
        /// </list>
        /// </summary>
        internal static bool FuzzyContains(string actual, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (string.IsNullOrEmpty(actual)) return false;

            // 1) Direct Contains
            if (actual.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

            var normA = NormalizeArg(actual);
            var normF = NormalizeArg(filter);

            // 2) Normalized Contains
            if (normA.Contains(normF, StringComparison.OrdinalIgnoreCase))
                return true;

            // 3) Plural/singular tolerance
            var singF = StripPlural(normF);
            var singA = StripPlural(normA);
            if (singA.Contains(singF, StringComparison.OrdinalIgnoreCase))
                return true;
            if (singF.Contains(singA, StringComparison.OrdinalIgnoreCase))
                return true;

            // 4) No-space collapsed ("supplyair" ↔ "supply air")
            var collapsedA = StripAllSpaces(normA);
            var collapsedF = StripAllSpaces(normF);
            if (collapsedA.Contains(collapsedF, StringComparison.OrdinalIgnoreCase))
                return true;

            // 5) Abbreviation expansion
            var expandedF = ExpandAbbreviations(normF);
            if (expandedF != normF)
            {
                if (normA.Contains(expandedF, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (StripAllSpaces(normA).Contains(StripAllSpaces(expandedF), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 6) Token overlap — all filter words present in actual (any order)
            var tokF = Tokenize(expandedF != normF ? expandedF : normF);
            var tokA = Tokenize(normA);
            if (tokF.Length > 1 && tokA.Length > 1 && AllTokensPresent(tokF, tokA))
                return true;

            return false;
        }

        // ──────────────────────────────────────────────────────────
        //  ResolveCategoryFilter
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve a user-provided category name to BuiltInCategory.
        /// Pipeline: alias → Revit display name → normalized → plural → abbreviation → token.
        /// </summary>
        internal static BuiltInCategory? ResolveCategoryFilter(Document doc, string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) return null;

            var trimmed = categoryName.Trim();

            if (CategoryAliases.TryGetValue(trimmed, out var alias))
                return alias;

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    return (BuiltInCategory)cat.Id.Value;
            }

            var norm = NormalizeArg(trimmed);

            if (CategoryAliases.TryGetValue(norm, out var normAlias))
                return normAlias;

            var sing = StripPlural(norm);
            if (CategoryAliases.TryGetValue(sing, out var singular))
                return singular;

            var expanded = ExpandAbbreviations(norm);
            if (expanded != norm && CategoryAliases.TryGetValue(expanded, out var expAlias))
                return expAlias;

            var collapsed = StripAllSpaces(norm);
            foreach (var kv in CategoryAliases)
            {
                if (StripAllSpaces(NormalizeArg(kv.Key)).Equals(collapsed, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }

            foreach (Category cat in doc.Settings.Categories)
            {
                if (FuzzyContains(cat.Name, trimmed))
                    return (BuiltInCategory)cat.Id.Value;
            }

            return null;
        }

        // ──────────────────────────────────────────────────────────
        //  MatchesSystem / MatchesCategoryName — public matchers
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Fuzzy match a user-provided system name against actual system name or classification.
        /// Covers: Contains, normalized, plural, no-space, abbreviations, token overlap.
        /// </summary>
        internal static bool MatchesSystem(string actualSystemName, string actualClassification, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (FuzzyContains(actualSystemName, filter)) return true;
            if (FuzzyContains(actualClassification, filter)) return true;
            return false;
        }

        /// <summary>
        /// Fuzzy category name match for fallback filtering.
        /// </summary>
        internal static bool MatchesCategoryName(string actualCategoryName, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return FuzzyContains(actualCategoryName, filter);
        }

        /// <summary>
        /// Build a FilteredElementCollector with optional early category filter.
        /// When the category cannot be resolved AND a non-empty categoryName was given,
        /// returns an empty collector to avoid scanning every element in the model.
        /// Callers that need the "soft fallback" behaviour should use MatchesCategoryName
        /// on the returned elements.
        /// </summary>
        internal static FilteredElementCollector BuildCollector(Document doc, string categoryName)
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            if (string.IsNullOrWhiteSpace(categoryName))
                return collector;

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (bic.HasValue)
                return collector.OfCategory(bic.Value);

            // Category name was provided but could not be resolved to a BuiltInCategory.
            // Instead of returning ALL elements (very slow on large models),
            // we try matching against category display names. If nothing at all matches,
            // callers use MatchesCategoryName for per-element fallback which is controlled.
            // Still apply at least one broad filter to avoid full-doc scan.
            // Return current collector; callers check needsCategoryFallback and filter.
            return collector;
        }

        /// <summary>true if a non-empty category was provided but could not be resolved to a BuiltInCategory.</summary>
        internal static bool NeedsCategoryFallback(Document doc, string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return false;
            return ResolveCategoryFilter(doc, categoryName) == null;
        }

        internal static string JsonError(string message) =>
            JsonSerializer.Serialize(new { error = message });

        internal static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        internal static XYZ GetElementCenter(Element elem)
        {
            if (elem.Location is LocationPoint lp) return lp.Point;
            if (elem.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);

            try
            {
                var bb = elem.get_BoundingBox(null);
                if (bb != null) return (bb.Min + bb.Max) / 2;
            }
            catch { }

            return null;
        }

        #region Argument parsing

        internal static T GetArg<T>(Dictionary<string, object> args, string key, T defaultValue = default)
        {
            if (args == null || !args.TryGetValue(key, out var val) || val == null)
                return defaultValue;

            if (val is T typed) return typed;

            if (val is JsonElement je)
            {
                if (typeof(T) == typeof(string)) return (T)(object)je.GetString();
                if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
                if (typeof(T) == typeof(long)) return (T)(object)je.GetInt64();
                if (typeof(T) == typeof(double)) return (T)(object)je.GetDouble();
                if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
            }

            try { return (T)Convert.ChangeType(val, typeof(T)); }
            catch { return defaultValue; }
        }

        internal static List<long> GetArgLongArray(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var val) || val == null) return null;
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetInt64()).ToList();
            return null;
        }

        internal static List<string> GetArgStringArray(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var val) || val == null) return null;
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetString()).ToList();
            return null;
        }

        #endregion

        #region MEP Connector Utilities

        /// <summary>
        /// Computes recommended extension length from a connector, based on its size.
        /// Round connectors: 4 * Radius (min 1 ft).
        /// Rectangular: 2 * Max(Width, Height) (min 1 ft).
        /// From DCMvn connector patterns.
        /// </summary>
        internal static double GetExtensionLengthFt(Connector conn)
        {
            if (conn.Shape == ConnectorProfileType.Round)
                return Math.Max(conn.Radius * 4, 1.0);
            return Math.Max(Math.Max(conn.Width, conn.Height) * 2, 1.0);
        }

        /// <summary>
        /// Checks if two connectors are properly aligned (facing each other).
        /// Uses BasisZ DotProduct: value &lt; -0.5 means connectors face each other.
        /// </summary>
        internal static bool AreConnectorsAligned(Connector c1, Connector c2)
        {
            return c1.CoordinateSystem.BasisZ.DotProduct(c2.CoordinateSystem.BasisZ) < -0.5;
        }

        /// <summary>
        /// Checks if connector direction is pointing up (vertical, Z > 0).
        /// </summary>
        internal static bool IsConnectorPointingUp(Connector conn)
        {
            var dir = conn.CoordinateSystem.BasisZ;
            return Math.Abs(dir.X) < 0.001 && Math.Abs(dir.Y) < 0.001 && dir.Z > 0;
        }

        /// <summary>
        /// Checks if connector direction is pointing down (vertical, Z &lt; 0).
        /// </summary>
        internal static bool IsConnectorPointingDown(Connector conn)
        {
            var dir = conn.CoordinateSystem.BasisZ;
            return Math.Abs(dir.X) < 0.001 && Math.Abs(dir.Y) < 0.001 && dir.Z < 0;
        }

        private static readonly string[][] DuctTypeKeywords = new[]
        {
            new[] { "round", "rund", "круглый", "원형", "redondo", "圆形", "丸形", "okrągły", "kulatý" },
            new[] { "rectangular", "rechteckig", "прямоугольный", "직사각형", "rectangular", "矩形", "長方形", "prostokątny", "obdélníkový" },
            new[] { "oval", "овальный", "타원형", "ovalado", "椭圆", "楕円", "owalny", "oválný" }
        };

        /// <summary>
        /// Resolves DuctType ID for a given shape, supporting multilingual family names.
        /// Searches through all DuctTypes and classifies by family name keywords in
        /// EN, DE, RU, KR, ES, ZH, JP, PL, CZ.
        /// </summary>
        internal static ElementId GetDuctTypeForShape(Document doc, ConnectorProfileType shape)
        {
            var ductTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Mechanical.DuctType))
                .ToElements();

            int targetIndex = shape switch
            {
                ConnectorProfileType.Round => 0,
                ConnectorProfileType.Rectangular => 1,
                ConnectorProfileType.Oval => 2,
                _ => 0
            };

            foreach (var dt in ductTypes)
            {
                var famName = dt.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM)?.AsString() ?? "";
                var lower = famName.ToLowerInvariant();
                if (DuctTypeKeywords[targetIndex].Any(kw => lower.Contains(kw)))
                    return dt.Id;
            }

            return ductTypes.FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
        }

        #endregion

        /// <summary>
        /// Universal parameter setter supporting String, Integer (incl. YesNo), Double, ElementId.
        /// Shared across ExportSkill, ModifySkill, and others to avoid duplication.
        /// </summary>
        internal static bool SetParamValue(Parameter param, string value)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value ?? "");
                        return true;
                    case StorageType.Integer:
                        if (param.Definition?.GetDataType() == SpecTypeId.Boolean.YesNo)
                        {
                            var lower = (value ?? "").ToLowerInvariant();
                            param.Set(lower is "yes" or "1" or "true" or "có" or "co" ? 1 : 0);
                            return true;
                        }
                        if (int.TryParse(value, out int iv)) { param.Set(iv); return true; }
                        return false;
                    case StorageType.Double:
                        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double dv))
                        { param.Set(dv); return true; }
                        return param.SetValueString(value);
                    case StorageType.ElementId:
                        if (long.TryParse(value, out long eid)) { param.Set(new ElementId(eid)); return true; }
                        return false;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        internal static string ValidateOutputPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            try
            {
                filePath = Path.GetFullPath(filePath);
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var temp = Path.GetTempPath();

                foreach (var allowed in new[] { desktop, docs, downloads, temp })
                {
                    if (string.IsNullOrEmpty(allowed)) continue;
                    var normalized = allowed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (filePath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (filePath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) &&
                        filePath.Length > normalized.Length &&
                        (filePath[normalized.Length] == Path.DirectorySeparatorChar || filePath[normalized.Length] == Path.AltDirectorySeparatorChar))
                        return null;
                }
                return $"Path '{filePath}' is outside allowed directories (Desktop, Documents, Downloads, Temp).";
            }
            catch (Exception ex) { return $"Invalid path: {ex.Message}"; }
        }
    }
}
