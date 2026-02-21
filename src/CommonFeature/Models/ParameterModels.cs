using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace CommonFeature.Models
{
    #region Enums

    /// <summary>
    /// Storage type for parameter values
    /// </summary>
    public enum ParamStorageType
    {
        None,
        String,
        Integer,
        Double,
        ElementId
    }

    /// <summary>
    /// Parameter group category (from Revit BuiltInParameterGroup)
    /// </summary>
    public enum ParamGroupType
    {
        Text,
        Dimensions,
        IdentityData,
        Constraints,
        Graphics,
        Materials,
        Phasing,
        Structural,
        Mechanical,
        Electrical,
        Plumbing,
        Other
    }

    /// <summary>
    /// Update mode for batch operations
    /// </summary>
    public enum UpdateMode
    {
        Direct,         // Set exact value
        Formula,        // Apply formula: +10, *1.5, prefix_, _suffix
        Transfer,       // Copy from another parameter
        Map             // Map from source param to target param
    }

    /// <summary>
    /// Difference type for comparison
    /// </summary>
    public enum DifferenceType
    {
        Same,
        Different,
        MissingInFirst,
        MissingInSecond,
        Empty
    }

    /// <summary>
    /// Validation type for audit
    /// </summary>
    public enum ValidationType
    {
        NotEmpty,       // Parameter must have a value
        Format,         // Value must match regex pattern
        Range,          // Numeric value must be in range
        AllowedList,    // Value must be in allowed list
        Unique          // Value must be unique within scope
    }

    /// <summary>
    /// Validation severity level
    /// </summary>
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    #endregion

    #region Parameter Definition & Metadata

    /// <summary>
    /// Definition of a parameter with all metadata
    /// </summary>
    public class ParameterDefinition : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Guid { get; set; }              // For Shared Parameters
        public bool IsInstance { get; set; }          // true = Instance, false = Type
        public bool IsReadOnly { get; set; }
        public bool IsBuiltIn { get; set; }
        public bool IsShared { get; set; }
        public ParamGroupType GroupType { get; set; }
        public ParamStorageType StorageType { get; set; }
        public string UnitType { get; set; }          // For display formatting
        public string GroupName { get; set; }         // Revit's group name

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public string TypeLabel => IsInstance ? "(I)" : "(T)";
        public string DisplayName => $"{Name} {TypeLabel}";
        public string UniqueKey => $"{Name}|{(IsInstance ? "I" : "T")}";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Group node for TreeView display
    /// </summary>
    public class ParameterGroupNode : INotifyPropertyChanged
    {
        public string GroupName { get; set; }         // "Instance Parameters", "Type Parameters"
        public string SubGroupName { get; set; }      // "Text", "Dimensions", etc.
        public List<ParameterDefinition> Parameters { get; set; } = new();
        
        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
        }

        public int ParameterCount => Parameters?.Count ?? 0;
        public string DisplayHeader => $"{SubGroupName} ({ParameterCount})";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #region Parameter Values

    /// <summary>
    /// Parameter value with metadata and modification tracking
    /// </summary>
    public class ParameterValue : INotifyPropertyChanged
    {
        public string ParameterName { get; set; }
        public bool IsInstance { get; set; }
        public ParamStorageType StorageType { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsNull { get; set; }
        
        private string _stringValue;
        public string StringValue
        {
            get => _stringValue;
            set
            {
                var oldValue = _stringValue;
                _stringValue = value;
                if (oldValue != value && !string.IsNullOrEmpty(OriginalValue))
                {
                    IsModified = value != OriginalValue;
                }
                OnPropertyChanged(nameof(StringValue));
                OnPropertyChanged(nameof(IsModified));
            }
        }

        public object RawValue { get; set; }
        public string OriginalValue { get; set; }
        
        private bool _isModified;
        public bool IsModified
        {
            get => _isModified;
            set { _isModified = value; OnPropertyChanged(nameof(IsModified)); }
        }

        public string DisplayValue => IsNull ? "<null>" : (StringValue ?? "-");

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Element with all its parameter values for grid display
    /// </summary>
    public class ElementParameterData : INotifyPropertyChanged
    {
        public long ElementId { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string Category { get; set; }

        // Key = ParameterName|I or ParameterName|T
        public Dictionary<string, ParameterValue> Values { get; set; } = new();

        // Quick accessors
        public ParameterValue GetValue(string paramName, bool isInstance)
        {
            var key = $"{paramName}|{(isInstance ? "I" : "T")}";
            return Values.TryGetValue(key, out var v) ? v : null;
        }

        public void SetValue(string paramName, bool isInstance, string newValue)
        {
            var key = $"{paramName}|{(isInstance ? "I" : "T")}";
            if (Values.TryGetValue(key, out var pv))
            {
                pv.StringValue = newValue;
                OnPropertyChanged(nameof(HasModifications));
            }
        }

        public bool HasModifications => Values.Values.Any(v => v.IsModified);
        public int ModifiedCount => Values.Values.Count(v => v.IsModified);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #region Update Operations

    /// <summary>
    /// Batch update request
    /// </summary>
    public class ParameterUpdateBatch
    {
        public List<ParameterBatchUpdateItem> Updates { get; set; } = new();
        public UpdateMode Mode { get; set; } = UpdateMode.Direct;
        public string Formula { get; set; }
        public string SourceParam { get; set; }
        public string TargetParam { get; set; }
    }

    /// <summary>
    /// Single update item in batch
    /// </summary>
    public class ParameterBatchUpdateItem
    {
        public long ElementId { get; set; }
        public string ParameterName { get; set; }
        public bool IsInstance { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
    }

    /// <summary>
    /// Result of update operation
    /// </summary>
    public class UpdateResult
    {
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        
        public bool HasErrors => Errors.Count > 0;
        public string Summary => $"Updated: {SuccessCount}, Failed: {FailedCount}, Skipped: {SkippedCount}";
    }

    #endregion

    #region Comparison

    /// <summary>
    /// Result of comparing two elements
    /// </summary>
    public class ComparisonResult
    {
        public long ElementId1 { get; set; }
        public long ElementId2 { get; set; }
        public string Element1Name { get; set; }
        public string Element2Name { get; set; }
        public List<ParameterDifference> Differences { get; set; } = new();
        public int MatchCount { get; set; }
        public int DiffCount => Differences.Count;
        
        public string Summary => $"Matched: {MatchCount}, Different: {DiffCount}";
    }

    /// <summary>
    /// Single parameter difference
    /// </summary>
    public class ParameterDifference : INotifyPropertyChanged
    {
        public string ParameterName { get; set; }
        public bool IsInstance { get; set; }
        public string Value1 { get; set; }
        public string Value2 { get; set; }
        public DifferenceType Type { get; set; }

        public string TypeLabel => IsInstance ? "(I)" : "(T)";
        public string StatusIcon => Type switch
        {
            DifferenceType.Same => "✓",
            DifferenceType.Different => "≠",
            DifferenceType.MissingInFirst => "⚠",
            DifferenceType.MissingInSecond => "⚠",
            DifferenceType.Empty => "○",
            _ => "?"
        };

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Element with empty parameter values
    /// </summary>
    public class EmptyParameterInfo
    {
        public long ElementId { get; set; }
        public string ElementName { get; set; }
        public string ParameterName { get; set; }
        public bool IsInstance { get; set; }
    }

    /// <summary>
    /// Duplicate parameter value group
    /// </summary>
    public class DuplicateGroup
    {
        public string ParameterName { get; set; }
        public string Value { get; set; }
        public List<long> ElementIds { get; set; } = new();
        public int Count => ElementIds.Count;
    }

    #endregion

    #region Validation / Audit

    /// <summary>
    /// Validation rule definition
    /// </summary>
    public class ValidationRule : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string ParameterName { get; set; }
        public bool IsInstance { get; set; } = true;
        public ValidationType Type { get; set; }
        public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;
        public string Message { get; set; }

        // For Format validation
        public string Pattern { get; set; }

        // For Range validation
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }

        // For AllowedList validation
        public List<string> AllowedValues { get; set; } = new();

        // For Unique validation
        public string UniqueScope { get; set; } = "All"; // "All", "Category", "Family"

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(nameof(IsEnabled)); }
        }

        public string TypeDisplay => Type switch
        {
            ValidationType.NotEmpty => "Not Empty",
            ValidationType.Format => $"Format: {Pattern}",
            ValidationType.Range => $"Range: {MinValue} - {MaxValue}",
            ValidationType.AllowedList => $"Allowed: {string.Join(", ", AllowedValues.Take(3))}...",
            ValidationType.Unique => $"Unique ({UniqueScope})",
            _ => Type.ToString()
        };

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Single validation issue
    /// </summary>
    public class ValidationIssue : INotifyPropertyChanged
    {
        public long ElementId { get; set; }
        public string ElementName { get; set; }
        public string ParameterName { get; set; }
        public bool IsInstance { get; set; }
        public string CurrentValue { get; set; }
        public string RuleId { get; set; }
        public ValidationType RuleType { get; set; }
        public ValidationSeverity Severity { get; set; }
        public string Message { get; set; }

        public string SeverityIcon => Severity switch
        {
            ValidationSeverity.Error => "🔴",
            ValidationSeverity.Warning => "🟡",
            ValidationSeverity.Info => "🔵",
            _ => "⚪"
        };

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Validation report summary
    /// </summary>
    public class ValidationReport
    {
        public int TotalElements { get; set; }
        public int ElementsWithIssues { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InfoCount { get; set; }
        public List<ValidationIssue> Issues { get; set; } = new();
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public string Summary => $"Elements: {TotalElements}, Issues: {Issues.Count} (Errors: {ErrorCount}, Warnings: {WarningCount})";

        private List<ValidationIssue> _cachedErrors;
        private List<ValidationIssue> _cachedWarnings;

        public List<ValidationIssue> Errors => _cachedErrors ??= Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        public List<ValidationIssue> Warnings => _cachedWarnings ??= Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();

        /// <summary>
        /// Call after modifying Issues to invalidate cached Errors/Warnings.
        /// </summary>
        public void InvalidateCache()
        {
            _cachedErrors = null;
            _cachedWarnings = null;
        }
    }

    #endregion

    #region Import/Export

    /// <summary>
    /// CSV import mapping configuration
    /// </summary>
    public class ImportMapping
    {
        public string KeyColumn { get; set; } = "ElementId";  // Column for matching elements
        public Dictionary<string, string> ColumnToParam { get; set; } = new(); // CSV column -> Revit param
        public bool HasHeaders { get; set; } = true;
        public string Delimiter { get; set; } = ",";
    }

    /// <summary>
    /// Import preview row
    /// </summary>
    public class ImportPreviewRow : INotifyPropertyChanged
    {
        public long ElementId { get; set; }
        public bool ElementExists { get; set; }
        public Dictionary<string, string> NewValues { get; set; } = new();
        public Dictionary<string, string> CurrentValues { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        
        public bool HasErrors => Errors.Count > 0;
        public bool IsValid => ElementExists && !HasErrors;

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) 
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    #endregion

    #region Formula Parser

    /// <summary>
    /// Formula parser and evaluator for batch updates
    /// </summary>
    public static class FormulaParser
    {
        /// <summary>
        /// Apply formula to a value
        /// </summary>
        public static string ApplyFormula(string formula, string currentValue, ParamStorageType storageType)
        {
            if (string.IsNullOrEmpty(formula)) return currentValue;
            formula = formula.Trim();

            // Numeric operations (for numeric types)
            if (storageType == ParamStorageType.Double || storageType == ParamStorageType.Integer)
            {
                if (double.TryParse(currentValue, out double numValue))
                {
                    // Addition: +10
                    if (Regex.IsMatch(formula, @"^\+\d+(\.\d+)?$"))
                    {
                        double addVal = double.Parse(formula.Substring(1));
                        double result = numValue + addVal;
                        return storageType == ParamStorageType.Integer 
                            ? ((int)result).ToString() 
                            : result.ToString();
                    }

                    // Subtraction: -5
                    if (Regex.IsMatch(formula, @"^-\d+(\.\d+)?$"))
                    {
                        double subVal = double.Parse(formula.Substring(1));
                        double result = numValue - subVal;
                        return storageType == ParamStorageType.Integer 
                            ? ((int)result).ToString() 
                            : result.ToString();
                    }

                    // Multiplication: *1.5
                    if (Regex.IsMatch(formula, @"^\*\d+(\.\d+)?$"))
                    {
                        double mulVal = double.Parse(formula.Substring(1));
                        double result = numValue * mulVal;
                        return storageType == ParamStorageType.Integer 
                            ? ((int)result).ToString() 
                            : result.ToString();
                    }

                    // Division: /2
                    if (Regex.IsMatch(formula, @"^/\d+(\.\d+)?$"))
                    {
                        double divVal = double.Parse(formula.Substring(1));
                        if (divVal == 0) return currentValue; // Avoid division by zero
                        double result = numValue / divVal;
                        return storageType == ParamStorageType.Integer 
                            ? ((int)result).ToString() 
                            : result.ToString();
                    }

                    // Round: {ROUND:2}
                    var roundMatch = Regex.Match(formula, @"^\{ROUND:(\d+)\}$", RegexOptions.IgnoreCase);
                    if (roundMatch.Success)
                    {
                        int decimals = int.Parse(roundMatch.Groups[1].Value);
                        return Math.Round(numValue, decimals).ToString();
                    }
                }
            }

            // String operations
            string strValue = currentValue ?? "";

            // Prefix: prefix_
            if (formula.EndsWith("_") && !formula.StartsWith("_"))
            {
                string prefix = formula.TrimEnd('_');
                return prefix + strValue;
            }

            // Suffix: _suffix
            if (formula.StartsWith("_") && !formula.EndsWith("_"))
            {
                string suffix = formula.TrimStart('_');
                return strValue + suffix;
            }

            // Uppercase: {UPPER}
            if (formula.Equals("{UPPER}", StringComparison.OrdinalIgnoreCase))
            {
                return strValue.ToUpperInvariant();
            }

            // Lowercase: {LOWER}
            if (formula.Equals("{LOWER}", StringComparison.OrdinalIgnoreCase))
            {
                return strValue.ToLowerInvariant();
            }

            // Trim: {TRIM}
            if (formula.Equals("{TRIM}", StringComparison.OrdinalIgnoreCase))
            {
                return strValue.Trim();
            }

            // Replace: {REPLACE:old:new}
            var replaceMatch = Regex.Match(formula, @"^\{REPLACE:(.+?):(.+?)\}$", RegexOptions.IgnoreCase);
            if (replaceMatch.Success)
            {
                string oldStr = replaceMatch.Groups[1].Value;
                string newStr = replaceMatch.Groups[2].Value;
                return strValue.Replace(oldStr, newStr);
            }

            // Regex replace: {REGEX:pattern:replacement}
            var regexMatch = Regex.Match(formula, @"^\{REGEX:(.+?):(.+?)\}$", RegexOptions.IgnoreCase);
            if (regexMatch.Success)
            {
                try
                {
                    string pattern = regexMatch.Groups[1].Value;
                    string replacement = regexMatch.Groups[2].Value;
                    return Regex.Replace(strValue, pattern, replacement);
                }
                catch
                {
                    return currentValue; // Invalid regex, return original
                }
            }

            // Substring: {SUB:start:length}
            var subMatch = Regex.Match(formula, @"^\{SUB:(\d+):(\d+)\}$", RegexOptions.IgnoreCase);
            if (subMatch.Success)
            {
                int start = int.Parse(subMatch.Groups[1].Value);
                int length = int.Parse(subMatch.Groups[2].Value);
                if (start < strValue.Length)
                {
                    length = Math.Min(length, strValue.Length - start);
                    return strValue.Substring(start, length);
                }
                return "";
            }

            // If no pattern matched, treat as direct value replacement
            return formula;
        }

        /// <summary>
        /// Validate formula syntax
        /// </summary>
        public static (bool IsValid, string Error) ValidateFormula(string formula)
        {
            if (string.IsNullOrWhiteSpace(formula))
                return (false, "Formula cannot be empty");

            formula = formula.Trim();

            // Check for valid patterns
            var patterns = new[]
            {
                @"^\+\d+(\.\d+)?$",           // +10, +1.5
                @"^-\d+(\.\d+)?$",            // -5, -2.5
                @"^\*\d+(\.\d+)?$",           // *2, *1.5
                @"^/\d+(\.\d+)?$",            // /2, /1.5
                @"^.+_$",                     // prefix_
                @"^_.+$",                     // _suffix
                @"^\{UPPER\}$",               // {UPPER}
                @"^\{LOWER\}$",               // {LOWER}
                @"^\{TRIM\}$",                // {TRIM}
                @"^\{ROUND:\d+\}$",           // {ROUND:2}
                @"^\{REPLACE:.+?:.+?\}$",     // {REPLACE:old:new}
                @"^\{REGEX:.+?:.+?\}$",       // {REGEX:pattern:replacement}
                @"^\{SUB:\d+:\d+\}$",         // {SUB:0:5}
            };

            foreach (var pattern in patterns)
            {
                if (Regex.IsMatch(formula, pattern, RegexOptions.IgnoreCase))
                    return (true, null);
            }

            // If no pattern matched, it's treated as a direct value (always valid)
            return (true, null);
        }

        /// <summary>
        /// Get formula description for tooltip
        /// </summary>
        public static string GetFormulaHelp()
        {
            return @"Formula Syntax:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
NUMERIC OPERATIONS:
  +10      → Add 10
  -5       → Subtract 5
  *1.5     → Multiply by 1.5
  /2       → Divide by 2
  {ROUND:2} → Round to 2 decimals

STRING OPERATIONS:
  prefix_  → Add prefix (text_)
  _suffix  → Add suffix (_text)
  {UPPER}  → UPPERCASE
  {LOWER}  → lowercase
  {TRIM}   → Remove spaces
  {REPLACE:old:new} → Replace text
  {REGEX:pattern:replacement}
  {SUB:0:5} → Substring (start:length)

Or enter any value to set directly.";
        }
    }

    #endregion

    #region Request DTOs for ExternalEvent

    /// <summary>
    /// Request types for Parameter Manager operations
    /// </summary>
    public enum ParameterRequestType
    {
        // Group 1: Browser
        GetAllParameters,
        GetParameterValues,

        // Group 2: Editor
        UpdateSingleValue,
        BatchUpdate,
        ApplyFormula,

        // Group 3: Transfer
        MapParameters,
        TransferBetweenElements,
        ImportFromCsv,
        ExportToCsv,

        // Group 4: Comparison
        CompareTwoElements,
        FindDifferences,
        FindEmptyValues,
        FindDuplicates,

        // Group 5: Audit
        ValidateParameters,
        SelectIssueElements,
        GenerateReport
    }

    /// <summary>
    /// Request DTO for Parameter Manager ExternalEvent
    /// </summary>
    public class ParameterRequest
    {
        public ParameterRequestType Type { get; set; }
        public List<long> ElementIds { get; set; }
        public List<string> ParameterNames { get; set; }
        public ParameterUpdateBatch UpdateBatch { get; set; }
        public string Formula { get; set; }
        public string SourceParam { get; set; }
        public string TargetParam { get; set; }
        public bool IsSourceInstance { get; set; }
        public bool IsTargetInstance { get; set; }
        public string FilePath { get; set; }
        public ImportMapping ImportMapping { get; set; }
        public List<ValidationRule> ValidationRules { get; set; }
        public long CompareElementId1 { get; set; }
        public long CompareElementId2 { get; set; }
        public long TemplateElementId { get; set; }

        #region Factory Methods

        public static ParameterRequest GetAllParameters(List<long> elementIds)
            => new() { Type = ParameterRequestType.GetAllParameters, ElementIds = elementIds };

        public static ParameterRequest GetParameterValues(List<long> elementIds, List<string> paramNames)
            => new() { Type = ParameterRequestType.GetParameterValues, ElementIds = elementIds, ParameterNames = paramNames };

        public static ParameterRequest BatchUpdate(ParameterUpdateBatch batch)
            => new() { Type = ParameterRequestType.BatchUpdate, UpdateBatch = batch };

        public static ParameterRequest ApplyFormula(List<long> elementIds, string paramName, bool isInstance, string formula)
            => new() 
            { 
                Type = ParameterRequestType.ApplyFormula, 
                ElementIds = elementIds, 
                ParameterNames = new List<string> { paramName },
                IsSourceInstance = isInstance,
                Formula = formula 
            };

        public static ParameterRequest MapParameters(List<long> elementIds, string sourceParam, bool sourceIsInstance, 
            string targetParam, bool targetIsInstance)
            => new() 
            { 
                Type = ParameterRequestType.MapParameters, 
                ElementIds = elementIds,
                SourceParam = sourceParam,
                IsSourceInstance = sourceIsInstance,
                TargetParam = targetParam,
                IsTargetInstance = targetIsInstance
            };

        public static ParameterRequest TransferBetweenElements(long sourceElementId, List<long> targetElementIds, 
            List<string> paramNames)
            => new() 
            { 
                Type = ParameterRequestType.TransferBetweenElements, 
                TemplateElementId = sourceElementId,
                ElementIds = targetElementIds,
                ParameterNames = paramNames
            };

        public static ParameterRequest ImportFromCsv(string filePath, ImportMapping mapping)
            => new() 
            { 
                Type = ParameterRequestType.ImportFromCsv, 
                FilePath = filePath,
                ImportMapping = mapping
            };

        public static ParameterRequest ExportToCsv(List<long> elementIds, List<string> paramNames, string filePath)
            => new() 
            { 
                Type = ParameterRequestType.ExportToCsv, 
                ElementIds = elementIds,
                ParameterNames = paramNames,
                FilePath = filePath
            };

        public static ParameterRequest CompareTwoElements(long elementId1, long elementId2)
            => new() 
            { 
                Type = ParameterRequestType.CompareTwoElements, 
                CompareElementId1 = elementId1,
                CompareElementId2 = elementId2
            };

        public static ParameterRequest FindDifferences(long templateElementId, List<long> elementIds, List<string> paramNames)
            => new() 
            { 
                Type = ParameterRequestType.FindDifferences, 
                TemplateElementId = templateElementId,
                ElementIds = elementIds,
                ParameterNames = paramNames
            };

        public static ParameterRequest FindEmptyValues(List<long> elementIds, List<string> paramNames)
            => new() 
            { 
                Type = ParameterRequestType.FindEmptyValues, 
                ElementIds = elementIds,
                ParameterNames = paramNames
            };

        public static ParameterRequest FindDuplicates(List<long> elementIds, string paramName, bool isInstance)
            => new() 
            { 
                Type = ParameterRequestType.FindDuplicates, 
                ElementIds = elementIds,
                ParameterNames = new List<string> { paramName },
                IsSourceInstance = isInstance
            };

        public static ParameterRequest ValidateParameters(List<long> elementIds, List<ValidationRule> rules)
            => new() 
            { 
                Type = ParameterRequestType.ValidateParameters, 
                ElementIds = elementIds,
                ValidationRules = rules
            };

        public static ParameterRequest SelectElements(List<long> elementIds)
            => new() 
            { 
                Type = ParameterRequestType.SelectIssueElements, 
                ElementIds = elementIds
            };

        #endregion
    }

    #endregion
}
