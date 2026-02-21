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

            if (elem.LevelId != null && elem.LevelId != ElementId.InvalidElementId)
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
    }
}
