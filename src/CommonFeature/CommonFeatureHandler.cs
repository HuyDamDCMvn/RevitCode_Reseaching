using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using HD.Core.Models;
using HD.Core.Services;
using CommonFeature.Graphics;
using CommonFeature.Handlers;
using CommonFeature.Models;
using CommonFeature.Views;

namespace CommonFeature
{
    /// <summary>
    /// Request types for CommonFeature operations.
    /// </summary>
    public enum RequestType
    {
        None,
        Isolate,
        IsolateElements,
        ResetIsolate,
        GetInformation,
        ShowParameter,
        ShowBoundary,
        GetParametersFromElements,
        GetParameterValues,
        UpdateParameterValues,
        SelectElements,
        CreateSectionBox
    }

    /// <summary>
    /// Request DTO for CommonFeature operations.
    /// </summary>
    public sealed class CommonFeatureRequest
    {
        public RequestType Type { get; }
        public List<long> ElementIds { get; }
        public List<string> ParameterNames { get; }
        public List<ParameterUpdateItem> Updates { get; }
        public string Description { get; init; }
        
        private CommonFeatureRequest(RequestType type, List<long> elementIds = null, 
            List<string> paramNames = null, List<ParameterUpdateItem> updates = null)
        {
            Type = type;
            ElementIds = elementIds;
            ParameterNames = paramNames;
            Updates = updates;
        }

        public static CommonFeatureRequest Isolate() => new(RequestType.Isolate);
        public static CommonFeatureRequest IsolateElements(List<long> elementIds, string description) 
            => new(RequestType.IsolateElements, elementIds) { Description = description };
        public static CommonFeatureRequest ResetIsolate() => new(RequestType.ResetIsolate);
        public static CommonFeatureRequest GetInformation() => new(RequestType.GetInformation);
        public static CommonFeatureRequest ShowParameter() => new(RequestType.ShowParameter);
        public static CommonFeatureRequest ShowBoundary() => new(RequestType.ShowBoundary);
        public static CommonFeatureRequest GetParametersFromElements(List<long> elementIds) 
            => new(RequestType.GetParametersFromElements, elementIds);
        public static CommonFeatureRequest GetParameterValues(List<long> elementIds, List<string> paramNames) 
            => new(RequestType.GetParameterValues, elementIds, paramNames);
        public static CommonFeatureRequest UpdateParameterValues(List<ParameterUpdateItem> updates)
            => new(RequestType.UpdateParameterValues, updates: updates);
        public static CommonFeatureRequest SelectElements(List<long> elementIds)
            => new(RequestType.SelectElements, elementIds);
        public static CommonFeatureRequest CreateSectionBox(List<long> elementIds)
            => new(RequestType.CreateSectionBox, elementIds);
    }

    /// <summary>
    /// External event handler - ONLY place that calls Revit API.
    /// </summary>
    public class CommonFeatureHandler : IExternalEventHandler
    {
        private CommonFeatureRequest _request;
        private readonly object _lock = new();
        
        // Store document reference for parameter callbacks
        private Document _currentDoc;
        private InfoWindow _currentInfoWindow;
        
        // ExternalEvent reference for async operations
        private ExternalEvent _externalEvent;

        /// <summary>
        /// Callback when operation completes.
        /// </summary>
        public event Action<string> OnOperationCompleted;

        /// <summary>
        /// Callback on error.
        /// </summary>
        public event Action<string> OnError;

        /// <summary>
        /// Set the ExternalEvent reference for async operations
        /// </summary>
        public void SetExternalEvent(ExternalEvent externalEvent)
        {
            _externalEvent = externalEvent;
        }

        public void SetRequest(CommonFeatureRequest request)
        {
            lock (_lock) { _request = request; }
        }

        public void Execute(UIApplication app)
        {
            CommonFeatureRequest request;
            lock (_lock) { request = _request; _request = null; }
            if (request == null) return;

            try
            {
                switch (request.Type)
                {
                    case RequestType.Isolate:
                        ExecuteIsolate(app);
                        break;
                    case RequestType.IsolateElements:
                        ExecuteIsolateElements(app, request.ElementIds, request.Description);
                        break;
                    case RequestType.ResetIsolate:
                        ExecuteResetIsolate(app);
                        break;
                    case RequestType.GetInformation:
                        ExecuteGetInformation(app);
                        break;
                    case RequestType.ShowParameter:
                        ExecuteShowParameter(app);
                        break;
                    case RequestType.ShowBoundary:
                        ExecuteShowBoundary(app);
                        break;
                    case RequestType.UpdateParameterValues:
                        ExecuteUpdateParameterValues(app, request.Updates);
                        break;
                    case RequestType.SelectElements:
                        ExecuteSelectElements(app, request.ElementIds);
                        break;
                    case RequestType.CreateSectionBox:
                        ExecuteCreateSectionBox(app, request.ElementIds);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// Get parameters from selected elements (called from UI)
        /// </summary>
        public List<ParameterInfo> GetParametersFromElements(Document doc, List<long> elementIds)
        {
            var paramInfos = new Dictionary<string, ParameterInfo>();

            foreach (var id in elementIds)
            {
                var elementId = new ElementId(id);
                var element = doc.GetElement(elementId);
                if (element == null) continue;

                // Get instance parameters
                foreach (Parameter param in element.Parameters)
                {
                    if (param.Definition == null) continue;
                    var name = param.Definition.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    
                    // Skip internal parameters
                    if (name.StartsWith("INVALID")) continue;
                    
                    if (!paramInfos.ContainsKey(name))
                    {
                        paramInfos[name] = new ParameterInfo(name, true);
                    }
                }

                // Get type parameters
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var elementType = doc.GetElement(typeId);
                    if (elementType != null)
                    {
                        foreach (Parameter param in elementType.Parameters)
                        {
                            if (param.Definition == null) continue;
                            var name = param.Definition.Name;
                            if (string.IsNullOrEmpty(name)) continue;
                            
                            // Skip internal parameters
                            if (name.StartsWith("INVALID")) continue;
                            
                            var key = name + "_Type";
                            if (!paramInfos.ContainsKey(key))
                            {
                                paramInfos[key] = new ParameterInfo(name, false);
                            }
                        }
                    }
                }
            }

            return paramInfos.Values.OrderBy(p => p.Name).ThenBy(p => p.IsInstance).ToList();
        }

        /// <summary>
        /// Get parameter values for all elements
        /// </summary>
        public Dictionary<long, ParameterValuesResult> GetParameterValues(Document doc, List<long> elementIds, List<string> paramNames)
        {
            var result = new Dictionary<long, ParameterValuesResult>();

            foreach (var id in elementIds)
            {
                var elementId = new ElementId(id);
                var element = doc.GetElement(elementId);
                if (element == null) continue;

                var paramResult = new ParameterValuesResult();

                foreach (var paramName in paramNames)
                {
                    string value = "-";
                    bool isReadOnly = true; // Default to read-only
                    string dataType = ParameterDataType.String; // Default
                    
                    // Try instance parameter first
                    var param = element.LookupParameter(paramName);
                    if (param != null)
                    {
                        value = GetParameterValueAsString(doc, param);
                        isReadOnly = IsParameterReadOnly(param);
                        dataType = GetParameterDataType(param);
                    }
                    else
                    {
                        // Try type parameter
                        var typeId = element.GetTypeId();
                        if (typeId != ElementId.InvalidElementId)
                        {
                            var elementType = doc.GetElement(typeId);
                            if (elementType != null)
                            {
                                param = elementType.LookupParameter(paramName);
                                if (param != null)
                                {
                                    value = GetParameterValueAsString(doc, param);
                                    isReadOnly = IsParameterReadOnly(param);
                                    dataType = GetParameterDataType(param);
                                }
                            }
                        }
                    }

                    paramResult.Values[paramName] = value;
                    paramResult.DataTypes[paramName] = dataType;
                    if (isReadOnly)
                    {
                        paramResult.ReadOnlyParams.Add(paramName);
                    }
                }

                result[id] = paramResult;
            }

            return result;
        }

        /// <summary>
        /// Check if a parameter is read-only (BuiltInParameter or IsReadOnly flag)
        /// </summary>
        private bool IsParameterReadOnly(Parameter param)
        {
            if (param == null) return true;
            
            // Check if parameter itself is read-only
            if (param.IsReadOnly) return true;
            
            // Check if it's a BuiltInParameter
            var definition = param.Definition;
            if (definition is InternalDefinition internalDef)
            {
                // BuiltInParameters have BuiltInParameter enum value
                if (internalDef.BuiltInParameter != BuiltInParameter.INVALID)
                {
                    // Some BuiltInParameters are editable, check IsReadOnly
                    return param.IsReadOnly;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Get the data type string for a parameter (for UI validation)
        /// </summary>
        private string GetParameterDataType(Parameter param)
        {
            if (param == null) return ParameterDataType.String;
            
            switch (param.StorageType)
            {
                case StorageType.String:
                    return ParameterDataType.String;
                    
                case StorageType.Integer:
                    // Check if it's a Yes/No parameter
                    if (param.Definition?.GetDataType() == SpecTypeId.Boolean.YesNo)
                    {
                        return ParameterDataType.YesNo;
                    }
                    return ParameterDataType.Integer;
                    
                case StorageType.Double:
                    return ParameterDataType.Double;
                    
                case StorageType.ElementId:
                    return ParameterDataType.ElementId;
                    
                default:
                    return ParameterDataType.String;
            }
        }

        private string GetParameterValueAsString(Document doc, Parameter param)
        {
            if (param == null || !param.HasValue) return "-";

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "-";
                case StorageType.Integer:
                    // Check if it's a Yes/No parameter
                    if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                    {
                        return param.AsInteger() == 1 ? "Yes" : "No";
                    }
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F2");
                case StorageType.ElementId:
                    var elemId = param.AsElementId();
                    if (elemId == ElementId.InvalidElementId) return "-";
                    // Try to get element name instead of just ID
                    if (doc != null)
                    {
                        var refElement = doc.GetElement(elemId);
                        if (refElement != null)
                        {
                            // Return element name if available
                            var name = refElement.Name;
                            if (!string.IsNullOrEmpty(name))
                            {
                                return name;
                            }
                        }
                    }
                    // Fallback to ID if element not found or no name
                    return elemId.Value.ToString();
                default:
                    return "-";
            }
        }

        /// <summary>
        /// Update parameter values in the Revit model
        /// </summary>
        public void UpdateParameterValues(Document doc, List<ParameterUpdateItem> updates)
        {
            if (doc == null || updates == null || updates.Count == 0) return;

            // Pre-validation checks
            if (doc.IsReadOnly)
            {
                ShowError("Document is read-only. Cannot update parameters.");
                return;
            }

            // Note: We don't check IsModifiable here because:
            // 1. When called from ExternalEvent handler, we're on Revit main thread
            // 2. IsModifiable may return false during event handling but we can still create transactions
            // 3. The Transaction.Start() will fail if there's a conflict, which we handle below

            int successCount = 0;
            int failCount = 0;
            int skippedCount = 0;
            var errors = new List<string>();
            var skipped = new List<string>();

            // Group updates by type parameter to avoid duplicate writes to same type
            var typeParamUpdates = new Dictionary<(ElementId typeId, string paramName), (ParameterUpdateItem update, List<long> elementIds)>();
            var instanceUpdates = new List<ParameterUpdateItem>();

            foreach (var update in updates)
            {
                if (update.IsInstance)
                {
                    instanceUpdates.Add(update);
                }
                else
                {
                    // Group type parameters by type ID
                    var elementId = new ElementId(update.ElementId);
                    var element = doc.GetElement(elementId);
                    if (element != null)
                    {
                        var typeId = element.GetTypeId();
                        if (typeId != ElementId.InvalidElementId)
                        {
                            var key = (typeId, update.ParameterName);
                            if (!typeParamUpdates.ContainsKey(key))
                            {
                                typeParamUpdates[key] = (update, new List<long> { update.ElementId });
                            }
                            else
                            {
                                typeParamUpdates[key].elementIds.Add(update.ElementId);
                            }
                        }
                    }
                }
            }

            using (var trans = new Transaction(doc, "Update Parameter Values"))
            {
                // Set failure handling options to prevent crash
                var failureOptions = trans.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new IgnoreWarningsPreprocessor());
                failureOptions.SetClearAfterRollback(true);
                trans.SetFailureHandlingOptions(failureOptions);

                try
                {
                    var status = trans.Start();
                    if (status != TransactionStatus.Started)
                    {
                        ShowError($"Failed to start transaction. Status: {status}");
                        return;
                    }

                    // Process instance parameters
                    foreach (var update in instanceUpdates)
                    {
                        var result = TryUpdateParameter(doc, update, errors, skipped);
                        if (result == UpdateResult.Success) successCount++;
                        else if (result == UpdateResult.Failed) failCount++;
                        else skippedCount++;
                    }

                    // Process type parameters (only once per type)
                    foreach (var kvp in typeParamUpdates)
                    {
                        var (update, elementIds) = kvp.Value;
                        var result = TryUpdateParameter(doc, update, errors, skipped);
                        if (result == UpdateResult.Success) successCount++;
                        else if (result == UpdateResult.Failed) failCount++;
                        else skippedCount++;
                    }

                    // Check if we have any successful updates before committing
                    if (successCount == 0 && failCount > 0)
                    {
                        trans.RollBack();
                        ShowWarning($"No values were updated.\n{failCount} value(s) failed.\n\nFirst few errors:\n" +
                            string.Join("\n", errors.Take(5)));
                        return;
                    }

                    var commitStatus = trans.Commit();
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        ShowError($"Transaction commit failed. Status: {commitStatus}");
                        return;
                    }
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    SafeRollback(trans);
                    ShowError($"Invalid operation: {ex.Message}\n\nThis may be caused by trying to modify a parameter that is controlled by Revit.");
                    return;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    SafeRollback(trans);
                    ShowError($"Invalid argument: {ex.Message}\n\nPlease check the value format.");
                    return;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    SafeRollback(trans);
                    ShowWarning("Operation was cancelled.");
                    return;
                }
                catch (Exception ex)
                {
                    SafeRollback(trans);
                    ShowError($"Unexpected error: {ex.Message}\n\nType: {ex.GetType().Name}");
                    return;
                }
            }

            // Show result
            ShowUpdateResult(successCount, failCount, skippedCount, errors, skipped);
        }

        private enum UpdateResult { Success, Failed, Skipped }

        private UpdateResult TryUpdateParameter(Document doc, ParameterUpdateItem update, 
            List<string> errors, List<string> skipped)
        {
            try
            {
                var elementId = new ElementId(update.ElementId);
                var element = doc.GetElement(elementId);
                
                // Check if element exists
                if (element == null)
                {
                    errors.Add($"Element {update.ElementId} not found");
                    return UpdateResult.Failed;
                }

                // Check if element is valid (not deleted)
                if (!element.IsValidObject)
                {
                    errors.Add($"Element {update.ElementId} is invalid or deleted");
                    return UpdateResult.Failed;
                }

                Parameter param = null;
                Element targetElement = element;

                if (update.IsInstance)
                {
                    // Instance parameter
                    param = element.LookupParameter(update.ParameterName);
                }
                else
                {
                    // Type parameter
                    var typeId = element.GetTypeId();
                    if (typeId == ElementId.InvalidElementId)
                    {
                        errors.Add($"Element {update.ElementId} has no type");
                        return UpdateResult.Failed;
                    }
                    
                    var elementType = doc.GetElement(typeId);
                    if (elementType == null)
                    {
                        errors.Add($"Type for element {update.ElementId} not found");
                        return UpdateResult.Failed;
                    }
                    
                    targetElement = elementType;
                    param = elementType.LookupParameter(update.ParameterName);
                }

                if (param == null)
                {
                    errors.Add($"Parameter '{update.ParameterName}' not found on element {update.ElementId}");
                    return UpdateResult.Failed;
                }

                // Check read-only status
                if (param.IsReadOnly)
                {
                    skipped.Add($"Parameter '{update.ParameterName}' is read-only");
                    return UpdateResult.Skipped;
                }

                // Check if parameter has value
                if (!param.HasValue && string.IsNullOrEmpty(update.NewValue))
                {
                    skipped.Add($"Parameter '{update.ParameterName}' has no value to update");
                    return UpdateResult.Skipped;
                }

                // Validate value before setting
                string validationError = ValidateParameterValue(param, update.NewValue);
                if (validationError != null)
                {
                    errors.Add($"'{update.ParameterName}' on element {update.ElementId}: {validationError}");
                    return UpdateResult.Failed;
                }

                // Set parameter value with safety checks
                bool success = SetParameterValueSafe(param, update.NewValue, out string setError);
                if (success)
                {
                    return UpdateResult.Success;
                }
                else
                {
                    errors.Add($"Failed to set '{update.ParameterName}' on element {update.ElementId}: {setError}");
                    return UpdateResult.Failed;
                }
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                errors.Add($"Invalid operation on element {update.ElementId}: {ex.Message}");
                return UpdateResult.Failed;
            }
            catch (Exception ex)
            {
                errors.Add($"Error on element {update.ElementId}: {ex.Message}");
                return UpdateResult.Failed;
            }
        }

        private string ValidateParameterValue(Parameter param, string newValue)
        {
            if (param == null) return "Parameter is null";

            // Check for empty value
            if (string.IsNullOrWhiteSpace(newValue) || newValue == "-")
            {
                // Empty is allowed for string parameters
                if (param.StorageType != StorageType.String)
                {
                    return "Empty value not allowed for non-text parameters";
                }
                return null;
            }

            switch (param.StorageType)
            {
                case StorageType.Integer:
                    // Check Yes/No parameters
                    if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                    {
                        var validValues = new[] { "yes", "no", "1", "0", "true", "false" };
                        if (!validValues.Contains(newValue.ToLower()))
                        {
                            return "Yes/No parameter requires: Yes, No, 1, 0, True, or False";
                        }
                    }
                    else if (!int.TryParse(newValue, out _))
                    {
                        return $"Invalid integer value: '{newValue}'";
                    }
                    break;

                case StorageType.Double:
                    // Try both direct parse and with unit string
                    if (!double.TryParse(newValue, out _))
                    {
                        // Check if it might be a valid unit string (e.g., "1000 mm")
                        // We'll let SetValueString handle this, but check for obviously invalid input
                        if (string.IsNullOrWhiteSpace(newValue))
                        {
                            return "Invalid numeric value";
                        }
                    }
                    break;

                case StorageType.ElementId:
                    if (!long.TryParse(newValue, out _))
                    {
                        return $"Invalid element ID: '{newValue}'";
                    }
                    break;
            }

            return null;
        }

        private void SafeRollback(Transaction trans)
        {
            try
            {
                if (trans.HasStarted() && !trans.HasEnded())
                {
                    trans.RollBack();
                }
            }
            catch
            {
                // Ignore rollback errors
            }
        }

        private void ShowError(string message)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        private void ShowWarning(string message)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "Update Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        private void ShowUpdateResult(int successCount, int failCount, int skippedCount, 
            List<string> errors, List<string> skipped)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Updated: {successCount} value(s)");
                
                if (failCount > 0)
                {
                    sb.AppendLine($"Failed: {failCount} value(s)");
                }
                
                if (skippedCount > 0)
                {
                    sb.AppendLine($"Skipped: {skippedCount} value(s)");
                }

                if (errors.Count > 0)
                {
                    sb.AppendLine("\nErrors:");
                    foreach (var error in errors.Take(5))
                    {
                        sb.AppendLine($"  - {error}");
                    }
                    if (errors.Count > 5)
                    {
                        sb.AppendLine($"  ... and {errors.Count - 5} more errors");
                    }
                }

                var icon = failCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
                var title = failCount > 0 ? "Update Completed with Errors" : "Update Complete";
                
                MessageBox.Show(sb.ToString(), title, MessageBoxButton.OK, icon);
            });
        }

        /// <summary>
        /// Failure preprocessor to ignore warnings during parameter update
        /// </summary>
        private class IgnoreWarningsPreprocessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                var failures = failuresAccessor.GetFailureMessages();
                foreach (var failure in failures)
                {
                    var severity = failure.GetSeverity();
                    
                    // For warnings, delete them to continue
                    if (severity == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                    }
                    // For errors that can be resolved, try to resolve
                    else if (severity == FailureSeverity.Error)
                    {
                        if (failure.HasResolutions())
                        {
                            failuresAccessor.ResolveFailure(failure);
                        }
                    }
                }
                
                return FailureProcessingResult.Continue;
            }
        }

        private bool SetParameterValueSafe(Parameter param, string newValue, out string error)
        {
            error = null;
            
            if (param == null)
            {
                error = "Parameter is null";
                return false;
            }
            
            if (param.IsReadOnly)
            {
                error = "Parameter is read-only";
                return false;
            }

            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        var strValue = (newValue == "-" || newValue == null) ? "" : newValue;
                        param.Set(strValue);
                        return true;

                    case StorageType.Integer:
                        // Handle Yes/No parameters
                        var dataType = param.Definition?.GetDataType();
                        if (dataType == SpecTypeId.Boolean.YesNo)
                        {
                            var lower = newValue?.ToLower() ?? "";
                            var boolValue = lower == "yes" || lower == "1" || lower == "true";
                            param.Set(boolValue ? 1 : 0);
                            return true;
                        }
                        
                        if (int.TryParse(newValue, out int intValue))
                        {
                            param.Set(intValue);
                            return true;
                        }
                        error = $"Cannot parse '{newValue}' as integer";
                        return false;

                    case StorageType.Double:
                        // First try to parse as double directly
                        if (double.TryParse(newValue, System.Globalization.NumberStyles.Any, 
                            System.Globalization.CultureInfo.InvariantCulture, out double doubleValue))
                        {
                            // Check for valid range (avoid extreme values that crash Revit)
                            if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                            {
                                error = "Invalid numeric value (NaN or Infinity)";
                                return false;
                            }
                            
                            // Check reasonable bounds (Revit has limits)
                            const double MaxRevitValue = 1e10;
                            if (Math.Abs(doubleValue) > MaxRevitValue)
                            {
                                error = $"Value {doubleValue} exceeds Revit's limits";
                                return false;
                            }
                            
                            param.Set(doubleValue);
                            return true;
                        }
                        
                        // Try SetValueString for unit conversion (e.g., "1000 mm")
                        try
                        {
                            var success = param.SetValueString(newValue);
                            if (!success)
                            {
                                error = $"Cannot parse '{newValue}' as numeric value with units";
                            }
                            return success;
                        }
                        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                        {
                            error = $"Invalid value format: '{newValue}'";
                            return false;
                        }

                    case StorageType.ElementId:
                        if (string.IsNullOrWhiteSpace(newValue) || newValue == "-")
                        {
                            param.Set(ElementId.InvalidElementId);
                            return true;
                        }
                        
                        if (long.TryParse(newValue, out long idValue))
                        {
                            var newElemId = new ElementId(idValue);
                            
                            // Validate that the element exists (optional, but safer)
                            // Note: Some ElementId parameters reference things other than elements
                            param.Set(newElemId);
                            return true;
                        }
                        error = $"Cannot parse '{newValue}' as element ID";
                        return false;

                    case StorageType.None:
                        error = "Parameter has no storage type";
                        return false;

                    default:
                        error = $"Unknown storage type: {param.StorageType}";
                        return false;
                }
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                error = $"Invalid operation: {ex.Message}";
                return false;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentOutOfRangeException ex)
            {
                error = $"Value out of range: {ex.Message}";
                return false;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                error = $"Invalid argument: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Error: {ex.Message}";
                return false;
            }
        }

        private void ExecuteIsolate(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var activeView = uidoc.ActiveView;
            
            if (activeView == null)
            {
                OnError?.Invoke("No active view");
                return;
            }

            // Show Isolate Window
            var isolateWindow = new IsolateWindow();
            
            // Setup callbacks for data loading - uses current active view (may change)
            isolateWindow.GetCategoriesCallback = () => 
            {
                var currentView = uidoc.ActiveView;
                return currentView != null ? GetCategoriesForIsolate(doc, currentView.Id) : new List<CategoryItem>();
            };
            
            isolateWindow.GetFamiliesCallback = (categoryName) => 
            {
                var currentView = uidoc.ActiveView;
                return currentView != null ? GetFamiliesForCategory(doc, currentView.Id, categoryName) : new List<FamilyItem>();
            };
            
            isolateWindow.GetTypesCallback = (categoryName, familyName) => 
            {
                var currentView = uidoc.ActiveView;
                return currentView != null ? GetFamilyTypesForFamily(doc, currentView.Id, categoryName, familyName) : new List<FamilyTypeItem>();
            };
            
            isolateWindow.GetParametersCallback = (categoryName, familyName, familyTypeName) => 
            {
                var currentView = uidoc.ActiveView;
                return currentView != null ? GetParametersForIsolate(doc, currentView.Id, categoryName, familyName, familyTypeName) : new List<string>();
            };
            
            isolateWindow.GetValuesCallback = (categoryName, familyName, familyTypeName, paramName) => 
            {
                var currentView = uidoc.ActiveView;
                return currentView != null ? GetParameterValuesForIsolate(doc, currentView.Id, categoryName, familyName, familyTypeName, paramName) : new List<ParameterValueItem>();
            };
            
            // Setup isolate callback - uses ExternalEvent to run on Revit main thread
            isolateWindow.IsolateCallback = (request) =>
            {
                // Queue request and raise ExternalEvent
                SetRequest(CommonFeatureRequest.IsolateElements(request.ElementIds, request.Description));
                _externalEvent?.Raise();
            };
            
            // Setup reset callback - uses ExternalEvent
            isolateWindow.ResetIsolateCallback = () =>
            {
                SetRequest(CommonFeatureRequest.ResetIsolate());
                _externalEvent?.Raise();
            };
            
            // Setup callback to get current view name (for detecting view change)
            isolateWindow.GetViewNameCallback = () =>
            {
                try { return uidoc.ActiveView?.Name ?? ""; }
                catch { return ""; }
            };
            
            // Setup callback to re-isolate on view change
            isolateWindow.ReIsolateCallback = (categoryName, familyName, familyTypeName, paramName, paramValue) =>
            {
                try
                {
                    var currentView = uidoc.ActiveView;
                    return currentView != null 
                        ? GetElementIdsForIsolate(doc, currentView.Id, categoryName, familyName, familyTypeName, paramName, paramValue) 
                        : new List<long>();
                }
                catch { return new List<long>(); }
            };
            
            // Load data and show window
            isolateWindow.LoadData();
            isolateWindow.Show();
        }
        
        /// <summary>
        /// Get element IDs matching the filter criteria in the specified view
        /// Used for re-isolating when view changes
        /// </summary>
        private List<long> GetElementIdsForIsolate(Document doc, ElementId viewId, 
            string categoryName, string familyName, string familyTypeName, string paramName, string paramValue)
        {
            var result = new List<long>(256); // Pre-allocate for performance
            
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            
            // Pre-compute filter flags
            bool filterCategory = !string.IsNullOrEmpty(categoryName);
            bool filterFamily = !string.IsNullOrEmpty(familyName);
            bool filterType = !string.IsNullOrEmpty(familyTypeName);
            bool filterParam = !string.IsNullOrEmpty(paramName) && !string.IsNullOrEmpty(paramValue);
            
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                
                // Filter by category
                if (filterCategory && elem.Category.Name != categoryName)
                    continue;
                
                // Filter by family
                if (filterFamily && GetFamilyName(doc, elem) != familyName)
                    continue;
                
                // Filter by family type
                if (filterType && GetFamilyTypeName(doc, elem) != familyTypeName)
                    continue;
                
                // Filter by parameter value
                if (filterParam)
                {
                    var param = elem.LookupParameter(paramName);
                    if (param == null) continue;
                    
                    var value = GetParameterValueAsString(doc, param);
                    if (string.IsNullOrEmpty(value) || value == "-") value = "(Empty)";
                    
                    if (value != paramValue)
                        continue;
                }
                
                result.Add(elem.Id.Value);
            }
            
            return result;
        }

        private void ExecuteIsolateElements(UIApplication app, List<long> elementIds, string description)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var activeView = uidoc.ActiveView;
            
            if (activeView == null)
            {
                OnError?.Invoke("No active view");
                return;
            }
            
            if (elementIds == null || elementIds.Count == 0)
            {
                OnError?.Invoke("No elements to isolate");
                return;
            }
            
            try
            {
                // Convert element IDs - use pre-allocated collection for better performance
                var ids = new List<ElementId>(elementIds.Count);
                foreach (var id in elementIds)
                {
                    ids.Add(new ElementId(id));
                }
                
                // Single transaction: Reset + Isolate for better performance
                using (var trans = new Transaction(doc, "Isolate Elements"))
                {
                    trans.Start();
                    
                    // First, reset any existing temporary isolation
                    activeView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    
                    // Then apply new isolation
                    activeView.IsolateElementsTemporary(ids);
                    
                    trans.Commit();
                }
                
                OnOperationCompleted?.Invoke($"Isolated {ids.Count} elements: {description}");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Isolate failed: {ex.Message}");
            }
        }

        private void ExecuteResetIsolate(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var activeView = uidoc.ActiveView;
            
            if (activeView == null)
            {
                OnError?.Invoke("No active view");
                return;
            }
            
            try
            {
                using (var trans = new Transaction(doc, "Reset Isolate"))
                {
                    trans.Start();
                    activeView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    trans.Commit();
                }
                
                OnOperationCompleted?.Invoke("Isolation reset");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Reset isolate failed: {ex.Message}");
            }
        }

        #region Isolate Helper Methods

        /// <summary>
        /// Get categories from elements visible in the specified view only
        /// </summary>
        private List<CategoryItem> GetCategoriesForIsolate(Document doc, ElementId viewId)
        {
            var result = new Dictionary<long, CategoryItem>();
            
            // Filter by view - only visible elements
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                var catId = elem.Category.Id.Value;
                var elemId = elem.Id.Value;
                
                if (result.TryGetValue(catId, out var item))
                {
                    item.ElementCount++;
                    item.ElementIds.Add(elemId);
                }
                else
                {
                    result[catId] = new CategoryItem
                    {
                        Name = elem.Category.Name,
                        CategoryId = catId,
                        ElementCount = 1,
                        ElementIds = new List<long>(16) { elemId }
                    };
                }
            }
            
            return result.Values.OrderBy(c => c.Name).ToList();
        }

        /// <summary>
        /// Get families from elements visible in the specified view only, filtered by category
        /// </summary>
        private List<FamilyItem> GetFamiliesForCategory(Document doc, ElementId viewId, string categoryName)
        {
            var result = new Dictionary<string, FamilyItem>(StringComparer.Ordinal);
            
            // Filter by view - only visible elements
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                if (elem.Category.Name != categoryName) continue;
                
                string familyName = GetFamilyName(doc, elem);
                if (string.IsNullOrEmpty(familyName)) continue;
                
                var elemId = elem.Id.Value;
                
                if (result.TryGetValue(familyName, out var item))
                {
                    item.ElementCount++;
                    item.ElementIds.Add(elemId);
                }
                else
                {
                    result[familyName] = new FamilyItem
                    {
                        FamilyName = familyName,
                        CategoryName = categoryName,
                        ElementCount = 1,
                        ElementIds = new List<long>(16) { elemId }
                    };
                }
            }
            
            return result.Values.OrderBy(f => f.FamilyName).ToList();
        }

        private string GetFamilyName(Document doc, Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol?.Family != null)
            {
                return fi.Symbol.Family.Name;
            }
            
            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    var familyParam = elemType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (familyParam != null && familyParam.HasValue)
                    {
                        return familyParam.AsString();
                    }
                    return elemType.Name;
                }
            }
            
            return elem.GetType().Name;
        }
        
        private string GetFamilyTypeName(Document doc, Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol != null)
            {
                return fi.Symbol.Name;
            }
            
            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    return elemType.Name;
                }
            }
            
            return "";
        }
        
        /// <summary>
        /// Get family types from elements visible in the specified view only, filtered by category and family
        /// </summary>
        private List<FamilyTypeItem> GetFamilyTypesForFamily(Document doc, ElementId viewId, string categoryName, string familyName)
        {
            var result = new Dictionary<string, FamilyTypeItem>(StringComparer.Ordinal);
            
            // Filter by view - only visible elements
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            
            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                if (elem.Category.Name != categoryName) continue;
                
                string elemFamilyName = GetFamilyName(doc, elem);
                if (elemFamilyName != familyName) continue;
                
                string typeName = GetFamilyTypeName(doc, elem);
                if (string.IsNullOrEmpty(typeName)) continue;
                
                var elemId = elem.Id.Value;
                
                if (result.TryGetValue(typeName, out var item))
                {
                    item.ElementCount++;
                    item.ElementIds.Add(elemId);
                }
                else
                {
                    result[typeName] = new FamilyTypeItem
                    {
                        TypeName = typeName,
                        FamilyName = familyName,
                        CategoryName = categoryName,
                        ElementCount = 1,
                        ElementIds = new List<long>(16) { elemId }
                    };
                }
            }
            
            return result.Values.OrderBy(t => t.TypeName).ToList();
        }

        /// <summary>
        /// Get parameters from elements visible in the specified view only
        /// </summary>
        private List<string> GetParametersForIsolate(Document doc, ElementId viewId, string categoryName, string familyName, string familyTypeName)
        {
            var parameters = new HashSet<string>(StringComparer.Ordinal);
            
            // Filter by view - only visible elements
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            
            // Take sample of elements (max 20) to get parameters - reduced for speed
            int count = 0;
            foreach (var elem in collector)
            {
                if (elem.Category == null || elem.Category.Name != categoryName) continue;
                if (!string.IsNullOrEmpty(familyName) && GetFamilyName(doc, elem) != familyName) continue;
                if (!string.IsNullOrEmpty(familyTypeName) && GetFamilyTypeName(doc, elem) != familyTypeName) continue;
                
                foreach (Parameter param in elem.Parameters)
                {
                    if (param.Definition == null) continue;
                    var name = param.Definition.Name;
                    if (!string.IsNullOrEmpty(name) && !name.StartsWith("INVALID"))
                    {
                        parameters.Add(name);
                    }
                }
                
                if (++count >= 20) break; // Reduced sample size for speed
            }
            
            return parameters.OrderBy(p => p).ToList();
        }

        /// <summary>
        /// Get parameter values from elements visible in the specified view only
        /// </summary>
        private List<ParameterValueItem> GetParameterValuesForIsolate(Document doc, ElementId viewId, string categoryName, string familyName, string familyTypeName, string parameterName)
        {
            var result = new Dictionary<string, ParameterValueItem>(StringComparer.Ordinal);
            
            // Filter by view - only visible elements
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            
            foreach (var elem in collector)
            {
                if (elem.Category == null || elem.Category.Name != categoryName) continue;
                if (!string.IsNullOrEmpty(familyName) && GetFamilyName(doc, elem) != familyName) continue;
                if (!string.IsNullOrEmpty(familyTypeName) && GetFamilyTypeName(doc, elem) != familyTypeName) continue;
                
                var param = elem.LookupParameter(parameterName);
                if (param == null) continue;
                
                string value = GetParameterValueAsString(doc, param);
                if (string.IsNullOrEmpty(value) || value == "-") value = "(Empty)";
                
                var elemId = elem.Id.Value;
                
                if (result.TryGetValue(value, out var item))
                {
                    item.ElementCount++;
                    item.ElementIds.Add(elemId);
                }
                else
                {
                    result[value] = new ParameterValueItem
                    {
                        Value = value,
                        ElementCount = 1,
                        ElementIds = new List<long>(16) { elemId }
                    };
                }
            }
            
            return result.Values.OrderBy(v => v.Value).ToList();
        }

        #endregion

        private void ExecuteUpdateParameterValues(UIApplication app, List<ParameterUpdateItem> updates)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            
            // Call the update method (now running on Revit main thread)
            UpdateParameterValues(doc, updates);
            
            // Notify InfoWindow to clear modifications
            _currentInfoWindow?.Dispatcher.Invoke(() =>
            {
                _currentInfoWindow?.OnUpdateCompleted();
            });
        }

        private void ExecuteGetInformation(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            _currentDoc = doc;

            // Collect ALL elements from the project
            var elementInfos = new List<ElementInfo>();

            // Get all element instances (not types) that have a Category
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            foreach (var element in collector)
            {
                // Only include elements with a valid Category
                if (element.Category == null) continue;
                
                // Include Model and Annotation elements
                var catType = element.Category.CategoryType;
                if (catType != CategoryType.Model && catType != CategoryType.Annotation)
                    continue;

                var info = GetElementInfo(doc, element);
                elementInfos.Add(info);
            }

            // Show window on UI thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var infoWindow = new InfoWindow();
                _currentInfoWindow = infoWindow;
                
                // Setup callbacks for parameter retrieval (synchronous, called during handler execution)
                infoWindow.GetParametersCallback = (ids) => GetParametersFromElements(_currentDoc, ids);
                infoWindow.GetParameterValuesCallback = (ids, names) => GetParameterValues(_currentDoc, ids, names);
                
                // Setup async update callback using ExternalEvent
                infoWindow.RaiseUpdateEvent = (updates) =>
                {
                    SetRequest(CommonFeatureRequest.UpdateParameterValues(updates));
                    _externalEvent?.Raise();
                };
                
                // Setup async select elements callback
                infoWindow.RaiseSelectElementsEvent = (elementIds) =>
                {
                    SetRequest(CommonFeatureRequest.SelectElements(elementIds));
                    _externalEvent?.Raise();
                };
                
                // Setup async create section box callback
                infoWindow.RaiseCreateSectionBoxEvent = (elementIds) =>
                {
                    SetRequest(CommonFeatureRequest.CreateSectionBox(elementIds));
                    _externalEvent?.Raise();
                };
                
                infoWindow.SetData(elementInfos);
                infoWindow.Show();
            });

            OnOperationCompleted?.Invoke($"Loaded {elementInfos.Count} element(s) from project");
        }

        private ElementInfo GetElementInfo(Document doc, Element element)
        {
            // Get Element ID
            long id = element.Id.Value;

            // Get Family Name and Type
            string familyName = "-";
            string familyType = "-";

            if (element is FamilyInstance fi)
            {
                var symbol = fi.Symbol;
                if (symbol != null)
                {
                    familyName = symbol.Family?.Name ?? "-";
                    familyType = symbol.Name ?? "-";
                }
            }
            else if (element.GetTypeId() != ElementId.InvalidElementId)
            {
                var elementType = doc.GetElement(element.GetTypeId());
                if (elementType != null)
                {
                    familyType = elementType.Name ?? "-";
                    
                    // Try to get family name from type
                    var familyNameParam = elementType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (familyNameParam != null && familyNameParam.HasValue)
                    {
                        familyName = familyNameParam.AsString() ?? "-";
                    }
                    else
                    {
                        familyName = elementType.GetType().Name;
                    }
                }
            }
            else
            {
                familyName = element.GetType().Name;
                familyType = element.Name ?? "-";
            }

            // Get Category
            string category = element.Category?.Name ?? "-";

            // Get Workset
            string workset = "-";
            if (doc.IsWorkshared)
            {
                var worksetId = element.WorksetId;
                if (worksetId != WorksetId.InvalidWorksetId)
                {
                    var ws = doc.GetWorksetTable().GetWorkset(worksetId);
                    workset = ws?.Name ?? "-";
                }
            }

            // Get Created By (EDITED_BY parameter stores last editor, CREATED_BY for creator)
            string createdBy = "-";
            string editedBy = "-";
            
            try
            {
                // Try to get Created By parameter
                var createdByParam = element.get_Parameter(BuiltInParameter.EDITED_BY);
                if (createdByParam != null && createdByParam.HasValue)
                {
                    editedBy = createdByParam.AsString() ?? "-";
                }

                // For worksharing, get the creator from WorksharingUtils
                if (doc.IsWorkshared)
                {
                    var wsTooltipInfo = WorksharingUtils.GetWorksharingTooltipInfo(doc, element.Id);
                    if (wsTooltipInfo != null)
                    {
                        createdBy = !string.IsNullOrEmpty(wsTooltipInfo.Creator) ? wsTooltipInfo.Creator : "-";
                        editedBy = !string.IsNullOrEmpty(wsTooltipInfo.LastChangedBy) ? wsTooltipInfo.LastChangedBy : editedBy;
                    }
                }
            }
            catch
            {
                // Ignore errors when getting user info
            }

            return new ElementInfo(id, familyName, familyType, category, workset, createdBy, editedBy);
        }

        private void ExecuteShowParameter(UIApplication app)
        {
            // Step 1: Validate UIApplication
            if (app == null)
            {
                OnError?.Invoke("UIApplication is null");
                return;
            }

            // Step 2: Validate UIDocument
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document. Please open a Revit project first.");
                return;
            }

            // Step 3: Validate Document
            var doc = uidoc.Document;
            if (doc == null)
            {
                OnError?.Invoke("Document is null");
                return;
            }

            // Step 4: Get current selection (safely)
            List<long> currentSelection;
            try
            {
                var selection = uidoc.Selection;
                if (selection == null)
                {
                    currentSelection = new List<long>();
                }
                else
                {
                    var elementIds = selection.GetElementIds();
                    currentSelection = elementIds?.Select(id => id.Value).ToList() ?? new List<long>();
                }
            }
            catch (Exception selEx)
            {
                OnError?.Invoke($"Failed to get selection: {selEx.Message}");
                currentSelection = new List<long>();
            }

            // Step 5: Create ExternalEvent handler
            Handlers.ParameterExternalHandler parameterHandler;
            ExternalEvent parameterEvent;
            try
            {
                parameterHandler = new Handlers.ParameterExternalHandler();
                parameterEvent = ExternalEvent.Create(parameterHandler);
                
                if (parameterEvent == null)
                {
                    OnError?.Invoke("Failed to create ExternalEvent");
                    return;
                }
            }
            catch (Exception evtEx)
            {
                OnError?.Invoke($"Failed to create event handler: {evtEx.Message}");
                return;
            }

            // Step 6: Store document path for change detection
            string originalDocPath;
            try
            {
                originalDocPath = doc.PathName ?? doc.Title ?? "Untitled";
            }
            catch
            {
                originalDocPath = "Unknown";
            }

            // Step 7: Create and show window
            try
            {
                var parameterWindow = new ParameterWindow();
                
                if (parameterWindow == null)
                {
                    OnError?.Invoke("Failed to create Parameter Window");
                    return;
                }

                parameterHandler.Window = parameterWindow;
                parameterWindow.Initialize(parameterHandler, parameterEvent);

                // Setup callback to get current selection from Revit
                parameterWindow.GetCurrentSelectionCallback = () =>
                {
                    try
                    {
                        // Validate uidoc is still valid
                        if (uidoc == null) return new List<long>();
                        
                        var currentDoc = uidoc.Document;
                        if (currentDoc == null || !currentDoc.IsValidObject) return new List<long>();

                        string currentPath = currentDoc.PathName ?? currentDoc.Title ?? "Untitled";
                        if (currentPath != originalDocPath)
                        {
                            // Document changed - close window safely
                            try
                            {
                                parameterWindow.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        System.Windows.MessageBox.Show(
                                            "Document has changed. Closing Parameter Manager window.",
                                            "Document Changed", MessageBoxButton.OK, MessageBoxImage.Information);
                                        parameterWindow.Close();
                                    }
                                    catch { /* Ignore close errors */ }
                                }));
                            }
                            catch { /* Ignore dispatcher errors */ }
                            return new List<long>();
                        }

                        var sel = uidoc.Selection;
                        if (sel == null) return new List<long>();
                        
                        var ids = sel.GetElementIds();
                        return ids?.Select(id => id.Value).ToList() ?? new List<long>();
                    }
                    catch 
                    { 
                        return new List<long>(); 
                    }
                };

                // Load initial selection if any (after window is loaded)
                if (currentSelection.Count > 0)
                {
                    parameterWindow.Loaded += (s, e) =>
                    {
                        try
                        {
                            parameterWindow.LoadElements(currentSelection);
                        }
                        catch (Exception loadEx)
                        {
                            System.Windows.MessageBox.Show(
                                $"Failed to load elements: {loadEx.Message}",
                                "Load Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    };
                }

                parameterWindow.Show();
                OnOperationCompleted?.Invoke($"Parameter Manager opened with {currentSelection.Count} elements");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to open Parameter Manager:\n\n" +
                               $"Error: {ex.Message}\n\n" +
                               $"Type: {ex.GetType().Name}\n\n" +
                               $"Stack: {ex.StackTrace?.Substring(0, Math.Min(500, ex.StackTrace?.Length ?? 0))}";
                               
                OnError?.Invoke($"Failed to open Parameter Manager: {ex.Message}");
                
                // Also show detailed error in MessageBox for debugging
                System.Windows.MessageBox.Show(errorMsg, "Parameter Manager Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteShowBoundary(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;

            // Get current selection
            var currentSelection = uidoc.Selection.GetElementIds()
                .Select(id => id.Value)
                .ToList();

            // Create graphics server
            var graphicsServer = new BoundaryGraphicsServer(doc);
            graphicsServer.Register();

            // Create external handler for boundary operations
            var boundaryHandler = new BoundaryExternalHandler();
            var boundaryEvent = ExternalEvent.Create(boundaryHandler);
            boundaryHandler.GraphicsServer = graphicsServer;

            // Show window on UI thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var boundaryWindow = new BoundaryWindow();
                boundaryHandler.Window = boundaryWindow;

                // Store document path to detect document changes
                string originalDocPath = doc.PathName ?? doc.Title;
                
                // Setup callbacks
                boundaryWindow.GetCurrentSelectionCallback = () =>
                {
                    try
                    {
                        // Check if document has changed
                        var currentDoc = uidoc.Document;
                        if (currentDoc == null || !currentDoc.IsValidObject) return new List<long>();
                        
                        string currentPath = currentDoc.PathName ?? currentDoc.Title;
                        if (currentPath != originalDocPath)
                        {
                            // Document changed - close window
                            boundaryWindow.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                System.Windows.MessageBox.Show(
                                    "Document has changed. Closing Boundary window.",
                                    "Document Changed", MessageBoxButton.OK, MessageBoxImage.Information);
                                boundaryWindow.Close();
                            }));
                            return new List<long>();
                        }
                        
                        return uidoc.Selection.GetElementIds()
                            .Select(id => id.Value)
                            .ToList();
                    }
                    catch { return new List<long>(); }
                };

                boundaryWindow.PickElementsCallback = () =>
                {
                    boundaryHandler.SetRequest(BoundaryExternalHandler.RequestType.PickElements);
                    boundaryEvent.Raise();
                };

                boundaryWindow.UpdatePreviewCallback = (settings) =>
                {
                    boundaryHandler.SetRequest(BoundaryExternalHandler.RequestType.UpdatePreview, settings);
                    boundaryEvent.Raise();
                };

                boundaryWindow.ClearPreviewCallback = () =>
                {
                    boundaryHandler.SetRequest(BoundaryExternalHandler.RequestType.ClearPreview);
                    boundaryEvent.Raise();
                };

                boundaryWindow.GetViewNameCallback = () =>
                {
                    try { return uidoc.ActiveView?.Name ?? ""; }
                    catch { return ""; }
                };

                // Cleanup when window closes
                boundaryWindow.Closed += (s, e) =>
                {
                    try
                    {
                        graphicsServer.ClearData();
                        graphicsServer.Unregister();
                        
                        // Only refresh if document is still valid
                        if (uidoc != null && uidoc.Document != null && uidoc.Document.IsValidObject)
                        {
                            try
                            {
                                uidoc.RefreshActiveView();
                            }
                            catch
                            {
                                // View refresh failed - not critical
                            }
                        }
                    }
                    catch
                    {
                        // Ignore cleanup errors - this happens when Revit is closing
                    }
                };

                // Set initial selection if any
                if (currentSelection.Count > 0)
                {
                    boundaryWindow.SetInitialSelection(currentSelection);
                }

                boundaryWindow.Show();
            });

            OnOperationCompleted?.Invoke("Show Boundary window opened");
        }
        
        /// <summary>
        /// Select elements in Revit model
        /// </summary>
        private void ExecuteSelectElements(UIApplication app, List<long> elementIds)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }
            
            if (elementIds == null || elementIds.Count == 0)
            {
                OnError?.Invoke("No elements to select");
                return;
            }
            
            try
            {
                var elemIds = elementIds.Select(id => new ElementId(id)).ToList();
                uidoc.Selection.SetElementIds(elemIds);
                OnOperationCompleted?.Invoke($"Selected {elemIds.Count} element(s)");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Select elements failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Create section box for selected elements
        /// </summary>
        private void ExecuteCreateSectionBox(UIApplication app, List<long> elementIds)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }
            
            var doc = uidoc.Document;
            
            if (elementIds == null || elementIds.Count == 0)
            {
                OnError?.Invoke("No elements selected for section box");
                return;
            }
            
            try
            {
                // Get bounding box of all selected elements
                BoundingBoxXYZ combinedBounds = null;
                
                foreach (var id in elementIds)
                {
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem == null) continue;
                    
                    var bb = elem.get_BoundingBox(null);
                    if (bb == null) continue;
                    
                    if (combinedBounds == null)
                    {
                        combinedBounds = new BoundingBoxXYZ
                        {
                            Min = bb.Min,
                            Max = bb.Max
                        };
                    }
                    else
                    {
                        combinedBounds.Min = new XYZ(
                            Math.Min(combinedBounds.Min.X, bb.Min.X),
                            Math.Min(combinedBounds.Min.Y, bb.Min.Y),
                            Math.Min(combinedBounds.Min.Z, bb.Min.Z));
                        combinedBounds.Max = new XYZ(
                            Math.Max(combinedBounds.Max.X, bb.Max.X),
                            Math.Max(combinedBounds.Max.Y, bb.Max.Y),
                            Math.Max(combinedBounds.Max.Z, bb.Max.Z));
                    }
                }
                
                if (combinedBounds == null)
                {
                    OnError?.Invoke("Could not calculate bounding box for elements");
                    return;
                }
                
                // Add some padding (offset)
                double offset = 1.0; // 1 foot padding
                combinedBounds.Min = new XYZ(
                    combinedBounds.Min.X - offset,
                    combinedBounds.Min.Y - offset,
                    combinedBounds.Min.Z - offset);
                combinedBounds.Max = new XYZ(
                    combinedBounds.Max.X + offset,
                    combinedBounds.Max.Y + offset,
                    combinedBounds.Max.Z + offset);
                
                using (var trans = new Transaction(doc, "Create Section Box"))
                {
                    trans.Start();
                    
                    View3D view3D = null;
                    
                    // Check if current view is 3D view
                    if (uidoc.ActiveView is View3D activeView3D && !activeView3D.IsTemplate)
                    {
                        view3D = activeView3D;
                    }
                    else
                    {
                        // Create new 3D view
                        var viewFamilyType = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
                        
                        if (viewFamilyType == null)
                        {
                            trans.RollBack();
                            OnError?.Invoke("Cannot find 3D view family type");
                            return;
                        }
                        
                        // Generate unique view name
                        string baseName = "Element Information View";
                        string viewName = baseName;
                        int counter = 1;
                        
                        // Check if view name already exists
                        var existingViews = new FilteredElementCollector(doc)
                            .OfClass(typeof(View3D))
                            .Cast<View3D>()
                            .Select(v => v.Name)
                            .ToHashSet();
                        
                        while (existingViews.Contains(viewName))
                        {
                            viewName = $"{baseName} {counter}";
                            counter++;
                        }
                        
                        view3D = View3D.CreateIsometric(doc, viewFamilyType.Id);
                        view3D.Name = viewName;
                        
                        // Set display style to Shaded
                        view3D.DisplayStyle = DisplayStyle.Shading;
                        
                        // Set detail level to Fine
                        view3D.DetailLevel = ViewDetailLevel.Fine;
                    }
                    
                    // Set section box
                    view3D.SetSectionBox(combinedBounds);
                    view3D.IsSectionBoxActive = true;
                    
                    trans.Commit();
                    
                    // Activate the 3D view
                    uidoc.ActiveView = view3D;
                    
                    // Zoom to fit the section box
                    try
                    {
                        // Get UIView for the active view
                        var uiViews = uidoc.GetOpenUIViews();
                        var uiView = uiViews.FirstOrDefault(v => v.ViewId == view3D.Id);
                        if (uiView != null)
                        {
                            // Zoom to fit using the section box bounds
                            uiView.ZoomToFit();
                        }
                    }
                    catch
                    {
                        // Zoom failed - not critical, continue
                    }
                    
                    OnOperationCompleted?.Invoke($"Section box created for {elementIds.Count} element(s) in '{view3D.Name}'");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Create section box failed: {ex.Message}");
            }
        }

        public string GetName() => "CommonFeature.Handler";
    }
}
