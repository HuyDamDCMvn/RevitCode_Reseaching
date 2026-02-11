using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace HDRevitLib
{
    /// <summary>
    /// Utility class for working with Revit parameters.
    /// Provides methods to get, set, and query parameter values.
    /// </summary>
    public static class ParameterLib
    {
        #region Get Parameter

        /// <summary>
        /// Get a parameter by BuiltInParameter.
        /// </summary>
        public static Parameter Get(Element element, BuiltInParameter bip)
        {
            return element?.get_Parameter(bip);
        }

        /// <summary>
        /// Get a parameter by name.
        /// </summary>
        public static Parameter Get(Element element, string parameterName)
        {
            if (element == null || string.IsNullOrEmpty(parameterName))
                return null;

            return element.LookupParameter(parameterName);
        }

        /// <summary>
        /// Get a parameter by GUID (shared parameter).
        /// </summary>
        public static Parameter Get(Element element, Guid guid)
        {
            return element?.get_Parameter(guid);
        }

        /// <summary>
        /// Get all parameters of an element.
        /// </summary>
        public static IList<Parameter> GetAll(Element element)
        {
            if (element == null) return new List<Parameter>();

            return element.Parameters
                .Cast<Parameter>()
                .ToList();
        }

        /// <summary>
        /// Get all parameters that have values (not empty).
        /// </summary>
        public static IList<Parameter> GetAllWithValues(Element element)
        {
            if (element == null) return new List<Parameter>();

            return element.Parameters
                .Cast<Parameter>()
                .Where(p => p.HasValue)
                .ToList();
        }

        /// <summary>
        /// Get all writable (modifiable) parameters.
        /// </summary>
        public static IList<Parameter> GetWritable(Element element)
        {
            if (element == null) return new List<Parameter>();

            return element.Parameters
                .Cast<Parameter>()
                .Where(p => !p.IsReadOnly)
                .ToList();
        }

        #endregion

        #region Get Parameter Value

        /// <summary>
        /// Get parameter value as string.
        /// </summary>
        public static string GetString(Element element, BuiltInParameter bip)
        {
            var param = Get(element, bip);
            return GetValueAsString(param);
        }

        /// <summary>
        /// Get parameter value as string by name.
        /// </summary>
        public static string GetString(Element element, string parameterName)
        {
            var param = Get(element, parameterName);
            return GetValueAsString(param);
        }

        /// <summary>
        /// Get parameter value as double.
        /// </summary>
        public static double? GetDouble(Element element, BuiltInParameter bip)
        {
            var param = Get(element, bip);
            return GetValueAsDouble(param);
        }

        /// <summary>
        /// Get parameter value as double by name.
        /// </summary>
        public static double? GetDouble(Element element, string parameterName)
        {
            var param = Get(element, parameterName);
            return GetValueAsDouble(param);
        }

        /// <summary>
        /// Get parameter value as integer.
        /// </summary>
        public static int? GetInt(Element element, BuiltInParameter bip)
        {
            var param = Get(element, bip);
            return GetValueAsInt(param);
        }

        /// <summary>
        /// Get parameter value as integer by name.
        /// </summary>
        public static int? GetInt(Element element, string parameterName)
        {
            var param = Get(element, parameterName);
            return GetValueAsInt(param);
        }

        /// <summary>
        /// Get parameter value as ElementId.
        /// </summary>
        public static ElementId GetElementId(Element element, BuiltInParameter bip)
        {
            var param = Get(element, bip);
            return GetValueAsElementId(param);
        }

        /// <summary>
        /// Get parameter value as ElementId by name.
        /// </summary>
        public static ElementId GetElementId(Element element, string parameterName)
        {
            var param = Get(element, parameterName);
            return GetValueAsElementId(param);
        }

        #endregion

        #region Get Value Helpers

        /// <summary>
        /// Get parameter value as string (handles all storage types).
        /// </summary>
        public static string GetValueAsString(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();

                case StorageType.Integer:
                    return param.AsInteger().ToString();

                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F4");

                case StorageType.ElementId:
                    return param.AsElementId()?.ToString();

                default:
                    return null;
            }
        }

        /// <summary>
        /// Get parameter value as double.
        /// </summary>
        public static double? GetValueAsDouble(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.Double:
                    return param.AsDouble();

                case StorageType.Integer:
                    return param.AsInteger();

                case StorageType.String:
                    if (double.TryParse(param.AsString(), out var result))
                        return result;
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Get parameter value as integer.
        /// </summary>
        public static int? GetValueAsInt(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.Integer:
                    return param.AsInteger();

                case StorageType.Double:
                    return (int)param.AsDouble();

                case StorageType.String:
                    if (int.TryParse(param.AsString(), out var result))
                        return result;
                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Get parameter value as ElementId.
        /// </summary>
        public static ElementId GetValueAsElementId(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            if (param.StorageType == StorageType.ElementId)
                return param.AsElementId();

            return null;
        }

        #endregion

        #region Set Parameter Value

        /// <summary>
        /// Set parameter string value.
        /// </summary>
        public static bool Set(Element element, BuiltInParameter bip, string value)
        {
            var param = Get(element, bip);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter string value by name.
        /// </summary>
        public static bool Set(Element element, string parameterName, string value)
        {
            var param = Get(element, parameterName);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter double value.
        /// </summary>
        public static bool Set(Element element, BuiltInParameter bip, double value)
        {
            var param = Get(element, bip);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter double value by name.
        /// </summary>
        public static bool Set(Element element, string parameterName, double value)
        {
            var param = Get(element, parameterName);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter integer value.
        /// </summary>
        public static bool Set(Element element, BuiltInParameter bip, int value)
        {
            var param = Get(element, bip);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter integer value by name.
        /// </summary>
        public static bool Set(Element element, string parameterName, int value)
        {
            var param = Get(element, parameterName);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter ElementId value.
        /// </summary>
        public static bool Set(Element element, BuiltInParameter bip, ElementId value)
        {
            var param = Get(element, bip);
            return SetValue(param, value);
        }

        /// <summary>
        /// Set parameter ElementId value by name.
        /// </summary>
        public static bool Set(Element element, string parameterName, ElementId value)
        {
            var param = Get(element, parameterName);
            return SetValue(param, value);
        }

        #endregion

        #region Set Value Helpers

        /// <summary>
        /// Set parameter value (string).
        /// </summary>
        public static bool SetValue(Parameter param, string value)
        {
            if (param == null || param.IsReadOnly) return false;

            try
            {
                if (param.StorageType == StorageType.String)
                {
                    return param.Set(value ?? string.Empty);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Set parameter value (double).
        /// </summary>
        public static bool SetValue(Parameter param, double value)
        {
            if (param == null || param.IsReadOnly) return false;

            try
            {
                if (param.StorageType == StorageType.Double)
                {
                    return param.Set(value);
                }
                else if (param.StorageType == StorageType.Integer)
                {
                    return param.Set((int)value);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Set parameter value (integer).
        /// </summary>
        public static bool SetValue(Parameter param, int value)
        {
            if (param == null || param.IsReadOnly) return false;

            try
            {
                if (param.StorageType == StorageType.Integer)
                {
                    return param.Set(value);
                }
                else if (param.StorageType == StorageType.Double)
                {
                    return param.Set((double)value);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Set parameter value (ElementId).
        /// </summary>
        public static bool SetValue(Parameter param, ElementId value)
        {
            if (param == null || param.IsReadOnly) return false;

            try
            {
                if (param.StorageType == StorageType.ElementId)
                {
                    return param.Set(value ?? ElementId.InvalidElementId);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Parameter Info

        /// <summary>
        /// Get parameter definition name.
        /// </summary>
        public static string GetName(Parameter param)
        {
            return param?.Definition?.Name;
        }

        /// <summary>
        /// Get parameter storage type.
        /// </summary>
        public static StorageType? GetStorageType(Parameter param)
        {
            return param?.StorageType;
        }

        /// <summary>
        /// Check if parameter is read-only.
        /// </summary>
        public static bool IsReadOnly(Parameter param)
        {
            return param?.IsReadOnly ?? true;
        }

        /// <summary>
        /// Check if parameter has a value.
        /// </summary>
        public static bool HasValue(Parameter param)
        {
            return param?.HasValue ?? false;
        }

        /// <summary>
        /// Check if parameter is a shared parameter.
        /// </summary>
        public static bool IsShared(Parameter param)
        {
            return param?.IsShared ?? false;
        }

        /// <summary>
        /// Get the GUID of a shared parameter.
        /// </summary>
        public static Guid? GetGuid(Parameter param)
        {
            if (param == null || !param.IsShared) return null;

            try
            {
                return param.GUID;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get parameter group.
        /// </summary>
        public static string GetGroupName(Parameter param)
        {
#if REVIT2025
            return param?.Definition?.GetGroupTypeId()?.ToString();
#else
            return param?.Definition?.ParameterGroup.ToString();
#endif
        }

        #endregion

        #region Copy Parameters

        /// <summary>
        /// Copy all matching parameters from source to target element.
        /// </summary>
        public static int CopyParameters(Element source, Element target)
        {
            if (source == null || target == null) return 0;

            int count = 0;
            foreach (Parameter srcParam in source.Parameters)
            {
                if (srcParam.IsReadOnly || !srcParam.HasValue) continue;

                var tgtParam = target.LookupParameter(srcParam.Definition.Name);
                if (tgtParam == null || tgtParam.IsReadOnly) continue;
                if (tgtParam.StorageType != srcParam.StorageType) continue;

                try
                {
                    switch (srcParam.StorageType)
                    {
                        case StorageType.String:
                            if (tgtParam.Set(srcParam.AsString() ?? string.Empty)) count++;
                            break;
                        case StorageType.Double:
                            if (tgtParam.Set(srcParam.AsDouble())) count++;
                            break;
                        case StorageType.Integer:
                            if (tgtParam.Set(srcParam.AsInteger())) count++;
                            break;
                        case StorageType.ElementId:
                            if (tgtParam.Set(srcParam.AsElementId())) count++;
                            break;
                    }
                }
                catch
                {
                    // Skip failed parameters
                }
            }

            return count;
        }

        /// <summary>
        /// Copy specific parameters by name from source to target element.
        /// </summary>
        public static int CopyParameters(Element source, Element target, IEnumerable<string> parameterNames)
        {
            if (source == null || target == null || parameterNames == null) return 0;

            int count = 0;
            foreach (var name in parameterNames)
            {
                var srcParam = source.LookupParameter(name);
                var tgtParam = target.LookupParameter(name);

                if (srcParam == null || tgtParam == null) continue;
                if (srcParam.IsReadOnly || !srcParam.HasValue) continue;
                if (tgtParam.IsReadOnly) continue;
                if (tgtParam.StorageType != srcParam.StorageType) continue;

                try
                {
                    switch (srcParam.StorageType)
                    {
                        case StorageType.String:
                            if (tgtParam.Set(srcParam.AsString() ?? string.Empty)) count++;
                            break;
                        case StorageType.Double:
                            if (tgtParam.Set(srcParam.AsDouble())) count++;
                            break;
                        case StorageType.Integer:
                            if (tgtParam.Set(srcParam.AsInteger())) count++;
                            break;
                        case StorageType.ElementId:
                            if (tgtParam.Set(srcParam.AsElementId())) count++;
                            break;
                    }
                }
                catch
                {
                    // Skip failed parameters
                }
            }

            return count;
        }

        #endregion

        #region Batch Operations

        /// <summary>
        /// Set the same parameter value on multiple elements.
        /// </summary>
        public static int SetOnMultiple(IEnumerable<Element> elements, string parameterName, string value)
        {
            if (elements == null || string.IsNullOrEmpty(parameterName)) return 0;

            int count = 0;
            foreach (var element in elements)
            {
                if (Set(element, parameterName, value)) count++;
            }
            return count;
        }

        /// <summary>
        /// Set the same parameter value on multiple elements.
        /// </summary>
        public static int SetOnMultiple(IEnumerable<Element> elements, string parameterName, double value)
        {
            if (elements == null || string.IsNullOrEmpty(parameterName)) return 0;

            int count = 0;
            foreach (var element in elements)
            {
                if (Set(element, parameterName, value)) count++;
            }
            return count;
        }

        /// <summary>
        /// Set the same parameter value on multiple elements.
        /// </summary>
        public static int SetOnMultiple(IEnumerable<Element> elements, string parameterName, int value)
        {
            if (elements == null || string.IsNullOrEmpty(parameterName)) return 0;

            int count = 0;
            foreach (var element in elements)
            {
                if (Set(element, parameterName, value)) count++;
            }
            return count;
        }

        /// <summary>
        /// Get parameter values from multiple elements as dictionary.
        /// </summary>
        public static Dictionary<ElementId, string> GetFromMultiple(
            IEnumerable<Element> elements, 
            string parameterName)
        {
            var result = new Dictionary<ElementId, string>();
            if (elements == null || string.IsNullOrEmpty(parameterName)) return result;

            foreach (var element in elements)
            {
                if (element == null) continue;
                var value = GetString(element, parameterName);
                result[element.Id] = value;
            }

            return result;
        }

        #endregion

        #region Parameter Comparison

        /// <summary>
        /// Check if two elements have the same parameter value.
        /// </summary>
        public static bool AreEqual(Element elem1, Element elem2, string parameterName)
        {
            if (elem1 == null || elem2 == null || string.IsNullOrEmpty(parameterName))
                return false;

            var param1 = Get(elem1, parameterName);
            var param2 = Get(elem2, parameterName);

            if (param1 == null || param2 == null) return false;
            if (param1.StorageType != param2.StorageType) return false;

            switch (param1.StorageType)
            {
                case StorageType.String:
                    return param1.AsString() == param2.AsString();
                case StorageType.Double:
                    return Math.Abs(param1.AsDouble() - param2.AsDouble()) < 1e-9;
                case StorageType.Integer:
                    return param1.AsInteger() == param2.AsInteger();
                case StorageType.ElementId:
                    return param1.AsElementId() == param2.AsElementId();
                default:
                    return false;
            }
        }

        /// <summary>
        /// Find elements where parameter equals a specific value.
        /// </summary>
        public static IList<Element> FindByValue(
            IEnumerable<Element> elements, 
            string parameterName, 
            string value)
        {
            if (elements == null || string.IsNullOrEmpty(parameterName))
                return new List<Element>();

            return elements
                .Where(e => GetString(e, parameterName) == value)
                .ToList();
        }

        /// <summary>
        /// Find elements where parameter contains a specific string.
        /// </summary>
        public static IList<Element> FindByContains(
            IEnumerable<Element> elements, 
            string parameterName, 
            string searchText,
            bool ignoreCase = true)
        {
            if (elements == null || string.IsNullOrEmpty(parameterName) || string.IsNullOrEmpty(searchText))
                return new List<Element>();

            var comparison = ignoreCase 
                ? StringComparison.OrdinalIgnoreCase 
                : StringComparison.Ordinal;

            return elements
                .Where(e =>
                {
                    var val = GetString(e, parameterName);
                    return val != null && val.IndexOf(searchText, comparison) >= 0;
                })
                .ToList();
        }

        #endregion

        #region Unit Conversion Helpers

        /// <summary>
        /// Get parameter value in millimeters (for length parameters).
        /// </summary>
        public static double? GetInMm(Element element, string parameterName)
        {
            var value = GetDouble(element, parameterName);
            if (value.HasValue)
            {
                return value.Value * 304.8; // feet to mm
            }
            return null;
        }

        /// <summary>
        /// Get parameter value in millimeters (for length parameters).
        /// </summary>
        public static double? GetInMm(Element element, BuiltInParameter bip)
        {
            var value = GetDouble(element, bip);
            if (value.HasValue)
            {
                return value.Value * 304.8; // feet to mm
            }
            return null;
        }

        /// <summary>
        /// Set parameter value from millimeters (for length parameters).
        /// </summary>
        public static bool SetFromMm(Element element, string parameterName, double valueMm)
        {
            double valueFeet = valueMm / 304.8;
            return Set(element, parameterName, valueFeet);
        }

        /// <summary>
        /// Set parameter value from millimeters (for length parameters).
        /// </summary>
        public static bool SetFromMm(Element element, BuiltInParameter bip, double valueMm)
        {
            double valueFeet = valueMm / 304.8;
            return Set(element, bip, valueFeet);
        }

        /// <summary>
        /// Get parameter value in square meters (for area parameters).
        /// </summary>
        public static double? GetAreaInM2(Element element, string parameterName)
        {
            var value = GetDouble(element, parameterName);
            if (value.HasValue)
            {
                return value.Value * 0.092903; // sq feet to sq meters
            }
            return null;
        }

        /// <summary>
        /// Get parameter value in square meters (for area parameters).
        /// </summary>
        public static double? GetAreaInM2(Element element, BuiltInParameter bip)
        {
            var value = GetDouble(element, bip);
            if (value.HasValue)
            {
                return value.Value * 0.092903; // sq feet to sq meters
            }
            return null;
        }

        /// <summary>
        /// Get parameter value in cubic meters (for volume parameters).
        /// </summary>
        public static double? GetVolumeInM3(Element element, string parameterName)
        {
            var value = GetDouble(element, parameterName);
            if (value.HasValue)
            {
                return value.Value * 0.0283168; // cubic feet to cubic meters
            }
            return null;
        }

        /// <summary>
        /// Get parameter value in cubic meters (for volume parameters).
        /// </summary>
        public static double? GetVolumeInM3(Element element, BuiltInParameter bip)
        {
            var value = GetDouble(element, bip);
            if (value.HasValue)
            {
                return value.Value * 0.0283168; // cubic feet to cubic meters
            }
            return null;
        }

        #endregion

        #region Common Parameters

        /// <summary>
        /// Get element Mark parameter value.
        /// </summary>
        public static string GetMark(Element element)
        {
            return GetString(element, BuiltInParameter.ALL_MODEL_MARK);
        }

        /// <summary>
        /// Set element Mark parameter value.
        /// </summary>
        public static bool SetMark(Element element, string mark)
        {
            return Set(element, BuiltInParameter.ALL_MODEL_MARK, mark);
        }

        /// <summary>
        /// Get element Comments parameter value.
        /// </summary>
        public static string GetComments(Element element)
        {
            return GetString(element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        }

        /// <summary>
        /// Set element Comments parameter value.
        /// </summary>
        public static bool SetComments(Element element, string comments)
        {
            return Set(element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, comments);
        }

        /// <summary>
        /// Get element Type Name.
        /// </summary>
        public static string GetTypeName(Element element)
        {
            return GetString(element, BuiltInParameter.ELEM_TYPE_PARAM);
        }

        /// <summary>
        /// Get element Family Name.
        /// </summary>
        public static string GetFamilyName(Element element)
        {
            if (element is FamilyInstance fi)
            {
                return fi.Symbol?.Family?.Name;
            }
            return GetString(element, BuiltInParameter.ELEM_FAMILY_PARAM);
        }

        /// <summary>
        /// Get element Level.
        /// </summary>
        public static Level GetLevel(Element element)
        {
            var levelId = GetElementId(element, BuiltInParameter.LEVEL_PARAM);
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                levelId = GetElementId(element, BuiltInParameter.FAMILY_LEVEL_PARAM);
            }
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                return null;
            }

            return element.Document.GetElement(levelId) as Level;
        }

        /// <summary>
        /// Get element Level Name.
        /// </summary>
        public static string GetLevelName(Element element)
        {
            var level = GetLevel(element);
            return level?.Name;
        }

        /// <summary>
        /// Get Wall/Floor/Roof thickness in feet.
        /// </summary>
        public static double? GetThickness(Element element)
        {
            if (element is Wall wall)
            {
                return wall.Width;
            }

            return GetDouble(element, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM)
                   ?? GetDouble(element, BuiltInParameter.ROOF_ATTR_THICKNESS_PARAM);
        }

        /// <summary>
        /// Get Wall/Floor/Roof thickness in millimeters.
        /// </summary>
        public static double? GetThicknessInMm(Element element)
        {
            var thickness = GetThickness(element);
            if (thickness.HasValue)
            {
                return thickness.Value * 304.8;
            }
            return null;
        }

        /// <summary>
        /// Get element Area in square feet.
        /// </summary>
        public static double? GetArea(Element element)
        {
            return GetDouble(element, BuiltInParameter.HOST_AREA_COMPUTED);
        }

        /// <summary>
        /// Get element Volume in cubic feet.
        /// </summary>
        public static double? GetVolume(Element element)
        {
            return GetDouble(element, BuiltInParameter.HOST_VOLUME_COMPUTED);
        }

        /// <summary>
        /// Get element Length in feet.
        /// </summary>
        public static double? GetLength(Element element)
        {
            return GetDouble(element, BuiltInParameter.CURVE_ELEM_LENGTH);
        }

        #endregion

        #region Parameter Dictionary Export

        /// <summary>
        /// Export all parameter values of an element to a dictionary.
        /// </summary>
        public static Dictionary<string, string> ToDictionary(Element element)
        {
            var dict = new Dictionary<string, string>();
            if (element == null) return dict;

            foreach (Parameter param in element.Parameters)
            {
                var name = param.Definition?.Name;
                if (string.IsNullOrEmpty(name)) continue;

                var value = GetValueAsString(param);
                dict[name] = value;
            }

            return dict;
        }

        /// <summary>
        /// Export specific parameters to a dictionary.
        /// </summary>
        public static Dictionary<string, string> ToDictionary(Element element, IEnumerable<string> parameterNames)
        {
            var dict = new Dictionary<string, string>();
            if (element == null || parameterNames == null) return dict;

            foreach (var name in parameterNames)
            {
                var value = GetString(element, name);
                dict[name] = value;
            }

            return dict;
        }

        #endregion
    }
}
