using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace HD.Core.Models
{
    /// <summary>
    /// Data model for element information display
    /// </summary>
    public class ElementInfo : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string FamilyName { get; set; }
        public string FamilyType { get; set; }
        public string Category { get; set; }
        public string Workset { get; set; }
        public string CreatedBy { get; set; }
        public string EditedBy { get; set; }

        /// <summary>Dynamic parameter values keyed by parameter name</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();

        /// <summary>Original parameter values (before editing)</summary>
        public Dictionary<string, string> OriginalParameters { get; set; } = new();

        /// <summary>Track which parameters have been modified</summary>
        public HashSet<string> ModifiedParameters { get; set; } = new();

        /// <summary>Track which parameters are read-only</summary>
        public HashSet<string> ReadOnlyParameters { get; set; } = new();

        /// <summary>Store parameter data types for validation</summary>
        public Dictionary<string, string> ParameterDataTypes { get; set; } = new();

        public event PropertyChangedEventHandler PropertyChanged;

        public ElementInfo() { }

        public ElementInfo(long id, string familyName, string familyType, string category, string workset, string createdBy, string editedBy)
        {
            Id = id;
            FamilyName = familyName ?? "-";
            FamilyType = familyType ?? "-";
            Category = category ?? "-";
            Workset = workset ?? "-";
            CreatedBy = createdBy ?? "-";
            EditedBy = editedBy ?? "-";
        }

        public string GetParameterValue(string paramName)
            => Parameters.TryGetValue(paramName, out var value) ? value : "-";

        public void SetParameterValue(string paramName, string newValue)
        {
            if (!OriginalParameters.ContainsKey(paramName) && Parameters.ContainsKey(paramName))
                OriginalParameters[paramName] = Parameters[paramName];

            Parameters[paramName] = newValue;

            if (OriginalParameters.TryGetValue(paramName, out var original))
            {
                if (original != newValue) ModifiedParameters.Add(paramName);
                else ModifiedParameters.Remove(paramName);
            }
            else
            {
                ModifiedParameters.Add(paramName);
            }
        }

        public bool IsParameterModified(string paramName) => ModifiedParameters.Contains(paramName);
        public bool IsParameterReadOnly(string paramName) => ReadOnlyParameters.Contains(paramName);
        public string GetParameterDataType(string paramName)
            => ParameterDataTypes.TryGetValue(paramName, out var dt) ? dt : ParameterDataType.String;

        public string ValidateValue(string paramName, string newValue)
        {
            var dataType = GetParameterDataType(paramName);
            if (string.IsNullOrWhiteSpace(newValue) || newValue == "-") return null;

            switch (dataType)
            {
                case ParameterDataType.Integer:
                    if (!int.TryParse(newValue, out _))
                        return $"'{paramName}' requires an integer value";
                    break;
                case ParameterDataType.Double:
                    if (!double.TryParse(newValue, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _) && !double.TryParse(newValue, out _))
                        return $"'{paramName}' requires a numeric value";
                    break;
                case ParameterDataType.YesNo:
                    var validValues = new[] { "yes", "no", "1", "0", "true", "false" };
                    if (!validValues.Contains(newValue.ToLower()))
                        return $"'{paramName}' requires Yes/No value";
                    break;
                case ParameterDataType.ElementId:
                    if (!long.TryParse(newValue, out _))
                        return $"'{paramName}' requires an Element ID";
                    break;
            }
            return null;
        }

        public void ClearModifications()
        {
            OriginalParameters.Clear();
            ModifiedParameters.Clear();
        }
    }

    /// <summary>
    /// Parameter info for selection
    /// </summary>
    public class ParameterInfo
    {
        public string Name { get; set; }
        public bool IsInstance { get; set; }
        public string TypeLabel => IsInstance ? "(Instance)" : "(Type)";
        public bool IsSelected { get; set; }

        public ParameterInfo(string name, bool isInstance)
        {
            Name = name;
            IsInstance = isInstance;
            IsSelected = false;
        }
    }

    /// <summary>
    /// DTO for parameter update request
    /// </summary>
    public class ParameterUpdateItem
    {
        public long ElementId { get; set; }
        public string ParameterName { get; set; }
        public string NewValue { get; set; }
        public bool IsInstance { get; set; }

        public ParameterUpdateItem(long elementId, string parameterName, string newValue, bool isInstance)
        {
            ElementId = elementId;
            ParameterName = parameterName;
            NewValue = newValue;
            IsInstance = isInstance;
        }
    }

    /// <summary>
    /// Result containing parameter values and metadata
    /// </summary>
    public class ParameterValuesResult
    {
        public Dictionary<string, string> Values { get; set; } = new();
        public HashSet<string> ReadOnlyParams { get; set; } = new();
        public Dictionary<string, string> DataTypes { get; set; } = new();
    }

    /// <summary>
    /// Parameter data type constants
    /// </summary>
    public static class ParameterDataType
    {
        public const string String = "String";
        public const string Integer = "Integer";
        public const string Double = "Double";
        public const string ElementId = "ElementId";
        public const string YesNo = "YesNo";
    }
}
