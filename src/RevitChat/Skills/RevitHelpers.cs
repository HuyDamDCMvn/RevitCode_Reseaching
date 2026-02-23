using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Try to resolve a category name to a BuiltInCategory for early filtering.
        /// Returns null if not matched.
        /// </summary>
        internal static BuiltInCategory? ResolveCategoryFilter(Document doc, string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) return null;

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    return (BuiltInCategory)cat.Id.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Build a FilteredElementCollector with optional early category filter.
        /// </summary>
        internal static FilteredElementCollector BuildCollector(Document doc, string categoryName)
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            var bic = ResolveCategoryFilter(doc, categoryName);
            if (bic.HasValue)
                collector = collector.OfCategory(bic.Value);

            return collector;
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
    }
}
