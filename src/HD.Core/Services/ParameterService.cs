using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using HD.Core.Models;

namespace HD.Core.Services
{
    /// <summary>
    /// Service for reading and writing Revit element parameters.
    /// </summary>
    public static class ParameterService
    {
        #region Get Parameter

        public static Parameter Get(Element element, BuiltInParameter bip)
            => element?.get_Parameter(bip);

        public static Parameter Get(Element element, string parameterName)
        {
            if (element == null || string.IsNullOrEmpty(parameterName)) return null;
            return element.LookupParameter(parameterName);
        }

        public static Parameter Get(Element element, Guid guid)
            => element?.get_Parameter(guid);

        public static IList<Parameter> GetAll(Element element)
        {
            if (element == null) return new List<Parameter>();
            return element.Parameters.Cast<Parameter>().ToList();
        }

        public static IList<Parameter> GetWritable(Element element)
        {
            if (element == null) return new List<Parameter>();
            return element.Parameters.Cast<Parameter>().Where(p => !p.IsReadOnly).ToList();
        }

        #endregion

        #region Get Value

        public static string GetValueAsString(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();
                case StorageType.Integer:
                    if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                        return param.AsInteger() == 1 ? "Yes" : "No";
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F4");
                case StorageType.ElementId:
                    var id = param.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) return null;
                    var doc = param.Element?.Document;
                    if (doc != null)
                    {
                        var refElem = doc.GetElement(id);
                        if (refElem != null) return refElem.Name ?? id.Value.ToString();
                    }
                    return id.Value.ToString();
                default:
                    return null;
            }
        }

        public static string GetString(Element element, BuiltInParameter bip)
            => GetValueAsString(Get(element, bip));

        public static string GetString(Element element, string parameterName)
            => GetValueAsString(Get(element, parameterName));

        public static int? GetInteger(Element element, BuiltInParameter bip)
        {
            var param = Get(element, bip);
            return param?.StorageType == StorageType.Integer ? param.AsInteger() : null;
        }

        public static double? GetDouble(Element element, BuiltInParameter bip)
        {
            var param = Get(element, bip);
            return param?.StorageType == StorageType.Double ? param.AsDouble() : null;
        }

        #endregion

        #region Set Value

        public static bool SetValue(Parameter param, string value)
        {
            if (param == null || param.IsReadOnly) return false;

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        return param.Set(value ?? "");

                    case StorageType.Integer:
                        if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                        {
                            var lower = value?.ToLower();
                            var intVal = (lower == "yes" || lower == "true" || lower == "1") ? 1 : 0;
                            return param.Set(intVal);
                        }
                        if (int.TryParse(value, out var i)) return param.Set(i);
                        return false;

                    case StorageType.Double:
                        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                            return param.Set(d);
                        if (double.TryParse(value, out d)) return param.Set(d);
                        return false;

                    case StorageType.ElementId:
                        if (long.TryParse(value, out var l))
                            return param.Set(new ElementId(l));
                        return false;

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetValue(Element element, string parameterName, string value)
            => SetValue(Get(element, parameterName), value);

        public static bool SetValue(Element element, BuiltInParameter bip, string value)
            => SetValue(Get(element, bip), value);

        #endregion

        #region Parameter Info

        public static string GetDataType(Parameter param)
        {
            if (param == null) return ParameterDataType.String;

            switch (param.StorageType)
            {
                case StorageType.Integer:
                    if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                        return ParameterDataType.YesNo;
                    return ParameterDataType.Integer;
                case StorageType.Double:
                    return ParameterDataType.Double;
                case StorageType.ElementId:
                    return ParameterDataType.ElementId;
                default:
                    return ParameterDataType.String;
            }
        }

        public static bool IsReadOnly(Parameter param)
            => param == null || param.IsReadOnly;

        public static bool IsBuiltIn(Parameter param)
        {
            if (param?.Definition == null) return false;
            try
            {
                var internalDef = param.Definition as InternalDefinition;
                return internalDef?.BuiltInParameter != BuiltInParameter.INVALID;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Collect Parameters for Filter

        public static List<string> GetParametersForFilter(Document doc, ElementId viewId, 
            string categoryName, string familyName, string typeName, int sampleSize = 20)
        {
            var paramNames = new HashSet<string>(StringComparer.Ordinal);

            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();

            int count = 0;
            foreach (var elem in collector)
            {
                if (count >= sampleSize) break;

                if (elem.Category == null) continue;
                if (!string.IsNullOrEmpty(categoryName) && elem.Category.Name != categoryName) continue;
                if (!string.IsNullOrEmpty(familyName) && ElementCollectorService.GetFamilyName(doc, elem) != familyName) continue;
                if (!string.IsNullOrEmpty(typeName) && ElementCollectorService.GetTypeName(doc, elem) != typeName) continue;

                foreach (Parameter param in elem.Parameters)
                {
                    if (param.Definition == null) continue;
                    var name = param.Definition.Name;
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith("INVALID"))
                        paramNames.Add(name);
                }

                count++;
            }

            return paramNames.OrderBy(n => n).ToList();
        }

        public static List<ParameterValueItem> GetParameterValues(Document doc, ElementId viewId,
            string categoryName, string familyName, string typeName, string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName)) return new List<ParameterValueItem>();

            var result = new Dictionary<string, ParameterValueItem>(StringComparer.Ordinal);

            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                if (!string.IsNullOrEmpty(categoryName) && elem.Category.Name != categoryName) continue;
                if (!string.IsNullOrEmpty(familyName) && ElementCollectorService.GetFamilyName(doc, elem) != familyName) continue;
                if (!string.IsNullOrEmpty(typeName) && ElementCollectorService.GetTypeName(doc, elem) != typeName) continue;

                var param = elem.LookupParameter(parameterName);
                if (param == null || !param.HasValue) continue;

                var value = GetValueAsString(param);
                if (string.IsNullOrEmpty(value)) value = "(Empty)";

                if (!result.TryGetValue(value, out var item))
                {
                    item = new ParameterValueItem
                    {
                        Value = value,
                        ElementCount = 0,
                        ElementIds = new List<long>(16)
                    };
                    result[value] = item;
                }

                item.ElementCount++;
                item.ElementIds.Add(elem.Id.Value);
            }

            return result.Values.OrderBy(v => v.Value).ToList();
        }

        #endregion
    }
}
