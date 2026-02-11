using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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
        GetInformation,
        ShowParameter,
        ShowBoundary,
        GetParametersFromElements,
        GetParameterValues,
        UpdateParameterValues
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
        
        private CommonFeatureRequest(RequestType type, List<long> elementIds = null, 
            List<string> paramNames = null, List<ParameterUpdateItem> updates = null)
        {
            Type = type;
            ElementIds = elementIds;
            ParameterNames = paramNames;
            Updates = updates;
        }

        public static CommonFeatureRequest Isolate() => new(RequestType.Isolate);
        public static CommonFeatureRequest GetInformation() => new(RequestType.GetInformation);
        public static CommonFeatureRequest ShowParameter() => new(RequestType.ShowParameter);
        public static CommonFeatureRequest ShowBoundary() => new(RequestType.ShowBoundary);
        public static CommonFeatureRequest GetParametersFromElements(List<long> elementIds) 
            => new(RequestType.GetParametersFromElements, elementIds);
        public static CommonFeatureRequest GetParameterValues(List<long> elementIds, List<string> paramNames) 
            => new(RequestType.GetParameterValues, elementIds, paramNames);
        public static CommonFeatureRequest UpdateParameterValues(List<ParameterUpdateItem> updates)
            => new(RequestType.UpdateParameterValues, updates: updates);
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
                    
                    // Try instance parameter first
                    var param = element.LookupParameter(paramName);
                    if (param != null)
                    {
                        value = GetParameterValueAsString(param);
                        isReadOnly = IsParameterReadOnly(param);
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
                                    value = GetParameterValueAsString(param);
                                    isReadOnly = IsParameterReadOnly(param);
                                }
                            }
                        }
                    }

                    paramResult.Values[paramName] = value;
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

        private string GetParameterValueAsString(Parameter param)
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

            // Check if document is currently being modified by another transaction
            // IsModifiable returns true when document CAN be modified (not in transaction)
            // So we check if it's false, meaning a transaction is active
            if (!doc.IsModifiable)
            {
                ShowError("Document is currently in another transaction. Please wait and try again.");
                return;
            }

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
            // TODO: Implement Isolate feature
            OnOperationCompleted?.Invoke("Isolate: On Developing");
        }

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
            // TODO: Implement Show Parameter feature
            OnOperationCompleted?.Invoke("Show Parameter: On Developing");
        }

        private void ExecuteShowBoundary(UIApplication app)
        {
            // TODO: Implement Show Boundary feature
            OnOperationCompleted?.Invoke("Show Boundary: On Developing");
        }

        public string GetName() => "CommonFeature.Handler";
    }
}
