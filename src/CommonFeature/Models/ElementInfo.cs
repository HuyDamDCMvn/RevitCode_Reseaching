using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace CommonFeature.Models
{
    /// <summary>
    /// Data model for element information display.
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
        
        /// <summary>
        /// Dynamic parameter values keyed by parameter name
        /// </summary>
        public Dictionary<string, string> Parameters { get; set; } = new();
        
        /// <summary>
        /// Original parameter values (before editing)
        /// </summary>
        public Dictionary<string, string> OriginalParameters { get; set; } = new();
        
        /// <summary>
        /// Track which parameters have been modified
        /// </summary>
        public HashSet<string> ModifiedParameters { get; set; } = new();
        
        /// <summary>
        /// Track which parameters are read-only (BuiltInParameter or non-editable)
        /// </summary>
        public HashSet<string> ReadOnlyParameters { get; set; } = new();
        
        /// <summary>
        /// Store parameter data types for validation
        /// </summary>
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
        
        /// <summary>
        /// Get parameter value by name
        /// </summary>
        public string GetParameterValue(string paramName)
        {
            return Parameters.TryGetValue(paramName, out var value) ? value : "-";
        }
        
        /// <summary>
        /// Set parameter value and track modification (does NOT raise PropertyChanged to avoid filter refresh)
        /// </summary>
        public void SetParameterValue(string paramName, string newValue)
        {
            // Store original value if not already stored
            if (!OriginalParameters.ContainsKey(paramName) && Parameters.ContainsKey(paramName))
            {
                OriginalParameters[paramName] = Parameters[paramName];
            }
            
            Parameters[paramName] = newValue;
            
            // Check if value is different from original
            if (OriginalParameters.TryGetValue(paramName, out var original))
            {
                if (original != newValue)
                {
                    ModifiedParameters.Add(paramName);
                }
                else
                {
                    ModifiedParameters.Remove(paramName);
                }
            }
            else
            {
                ModifiedParameters.Add(paramName);
            }
            
            // NOTE: We intentionally do NOT raise PropertyChanged here
            // because it would trigger ICollectionView filter refresh and cause the row to disappear
            // The cell display is updated manually via UpdateCellBackgrounds()
        }
        
        /// <summary>
        /// Check if a parameter has been modified
        /// </summary>
        public bool IsParameterModified(string paramName)
        {
            return ModifiedParameters.Contains(paramName);
        }
        
        /// <summary>
        /// Check if a parameter is read-only
        /// </summary>
        public bool IsParameterReadOnly(string paramName)
        {
            return ReadOnlyParameters.Contains(paramName);
        }
        
        /// <summary>
        /// Get the data type of a parameter
        /// </summary>
        public string GetParameterDataType(string paramName)
        {
            return ParameterDataTypes.TryGetValue(paramName, out var dataType) ? dataType : ParameterDataType.String;
        }
        
        /// <summary>
        /// Validate a value against the parameter's data type
        /// Returns null if valid, or an error message if invalid
        /// </summary>
        public string ValidateValue(string paramName, string newValue)
        {
            var dataType = GetParameterDataType(paramName);
            
            // Empty/null is generally allowed (will be converted appropriately)
            if (string.IsNullOrWhiteSpace(newValue) || newValue == "-")
            {
                return null;
            }
            
            switch (dataType)
            {
                case ParameterDataType.Integer:
                    if (!int.TryParse(newValue, out _))
                    {
                        return $"'{paramName}' requires an integer value (e.g., 1, 2, 100)";
                    }
                    break;
                    
                case ParameterDataType.Double:
                    // Allow numbers with optional decimal
                    if (!double.TryParse(newValue, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        // Also try current culture
                        if (!double.TryParse(newValue, out _))
                        {
                            return $"'{paramName}' requires a numeric value (e.g., 1.5, 100, 3.14)";
                        }
                    }
                    break;
                    
                case ParameterDataType.YesNo:
                    var lower = newValue.ToLower();
                    var validValues = new[] { "yes", "no", "1", "0", "true", "false" };
                    if (!validValues.Contains(lower))
                    {
                        return $"'{paramName}' requires Yes/No value (Yes, No, True, False, 1, 0)";
                    }
                    break;
                    
                case ParameterDataType.ElementId:
                    if (!long.TryParse(newValue, out _))
                    {
                        return $"'{paramName}' requires an Element ID (numeric value)";
                    }
                    break;
                    
                case ParameterDataType.String:
                default:
                    // String accepts any value
                    break;
            }
            
            return null; // Valid
        }
        
        /// <summary>
        /// Clear modification tracking after successful update
        /// </summary>
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
    /// Result containing parameter values and read-only status
    /// </summary>
    public class ParameterValuesResult
    {
        public Dictionary<string, string> Values { get; set; } = new();
        public HashSet<string> ReadOnlyParams { get; set; } = new();
        /// <summary>
        /// Parameter data types: "String", "Integer", "Double", "ElementId", "YesNo"
        /// </summary>
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
