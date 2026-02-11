using System.Collections.Generic;
using System.ComponentModel;

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
        /// Set parameter value and track modification
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
            
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Parameters[{paramName}]"));
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
    }
}
