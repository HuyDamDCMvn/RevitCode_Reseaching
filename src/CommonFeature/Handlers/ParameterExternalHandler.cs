using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommonFeature.Models;
using CommonFeature.Views;

namespace CommonFeature.Handlers
{
    /// <summary>
    /// External event handler for Parameter Manager operations.
    /// All Revit API calls happen here on the main thread.
    /// </summary>
    public class ParameterExternalHandler : IExternalEventHandler
    {
        #region Fields

        private ParameterRequest _request;
        private readonly object _lock = new();

        #endregion

        #region Properties

        /// <summary>
        /// Reference to the ParameterWindow for callbacks.
        /// </summary>
        public ParameterWindow Window { get; set; }

        #endregion

        #region Events

        /// <summary>
        /// Callback when parameters are loaded.
        /// </summary>
        public event Action<List<ParameterGroupNode>, List<ElementParameterData>> OnParametersLoaded;

        /// <summary>
        /// Callback when update completes.
        /// </summary>
        public event Action<UpdateResult> OnUpdateCompleted;

        /// <summary>
        /// Callback when comparison completes.
        /// </summary>
        public event Action<ComparisonResult> OnComparisonCompleted;

        /// <summary>
        /// Callback when empty values found.
        /// </summary>
        public event Action<List<EmptyParameterInfo>> OnEmptyValuesFound;

        /// <summary>
        /// Callback when duplicates found.
        /// </summary>
        public event Action<List<DuplicateGroup>> OnDuplicatesFound;

        /// <summary>
        /// Callback when validation completes.
        /// </summary>
        public event Action<ValidationReport> OnValidationCompleted;

        /// <summary>
        /// Callback on error.
        /// </summary>
        public event Action<string> OnError;

        /// <summary>
        /// Callback for status updates.
        /// </summary>
        public event Action<string> OnStatusUpdate;

        #endregion

        #region Public Methods

        /// <summary>
        /// Queue a request for execution on the Revit main thread.
        /// </summary>
        public void SetRequest(ParameterRequest request)
        {
            lock (_lock)
            {
                _request = request;
            }
        }

        #endregion

        #region IExternalEventHandler Implementation

        public void Execute(UIApplication app)
        {
            ParameterRequest request;
            lock (_lock)
            {
                request = _request;
                _request = null;
            }

            if (request == null) return;

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                RaiseError("No active document");
                return;
            }

            var doc = uidoc.Document;

            try
            {
                switch (request.Type)
                {
                    // Group 1: Browser
                    case ParameterRequestType.GetAllParameters:
                        ExecuteGetAllParameters(doc, request.ElementIds);
                        break;
                    case ParameterRequestType.GetParameterValues:
                        ExecuteGetParameterValues(doc, request.ElementIds, request.ParameterNames);
                        break;

                    // Group 2: Editor
                    case ParameterRequestType.BatchUpdate:
                        ExecuteBatchUpdate(doc, request.UpdateBatch);
                        break;
                    case ParameterRequestType.ApplyFormula:
                        ExecuteApplyFormula(doc, request.ElementIds, request.ParameterNames[0], 
                            request.IsSourceInstance, request.Formula);
                        break;

                    // Group 3: Transfer
                    case ParameterRequestType.MapParameters:
                        ExecuteMapParameters(doc, request.ElementIds, request.SourceParam, 
                            request.IsSourceInstance, request.TargetParam, request.IsTargetInstance);
                        break;
                    case ParameterRequestType.TransferBetweenElements:
                        ExecuteTransferBetweenElements(doc, request.TemplateElementId, 
                            request.ElementIds, request.ParameterNames);
                        break;
                    case ParameterRequestType.ExportToCsv:
                        ExecuteExportToCsv(doc, request.ElementIds, request.ParameterNames, request.FilePath);
                        break;

                    // Group 4: Comparison
                    case ParameterRequestType.CompareTwoElements:
                        ExecuteCompareTwoElements(doc, request.CompareElementId1, request.CompareElementId2);
                        break;
                    case ParameterRequestType.FindEmptyValues:
                        ExecuteFindEmptyValues(doc, request.ElementIds, request.ParameterNames);
                        break;
                    case ParameterRequestType.FindDuplicates:
                        ExecuteFindDuplicates(doc, request.ElementIds, request.ParameterNames[0], 
                            request.IsSourceInstance);
                        break;

                    // Group 5: Audit
                    case ParameterRequestType.ValidateParameters:
                        ExecuteValidateParameters(doc, request.ElementIds, request.ValidationRules);
                        break;
                    case ParameterRequestType.SelectIssueElements:
                        ExecuteSelectElements(uidoc, request.ElementIds);
                        break;
                }
            }
            catch (Exception ex)
            {
                RaiseError($"Error: {ex.Message}");
            }
        }

        public string GetName() => "ParameterExternalHandler";

        #endregion

        #region Group 1: Browser

        private void ExecuteGetAllParameters(Document doc, List<long> elementIds)
        {
            RaiseStatus("Loading parameters...");
            
            var paramDefs = new Dictionary<string, ParameterDefinition>();
            var elementDataList = new List<ElementParameterData>();

            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                var elemData = new ElementParameterData
                {
                    ElementId = id,
                    Category = element.Category?.Name ?? "-"
                };

                // Get family/type names
                if (element is FamilyInstance fi && fi.Symbol != null)
                {
                    elemData.FamilyName = fi.Symbol.Family?.Name ?? "-";
                    elemData.TypeName = fi.Symbol.Name ?? "-";
                }
                else
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var elemType = doc.GetElement(typeId);
                        var familyParam = elemType?.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                        elemData.FamilyName = familyParam?.AsString() ?? elemType?.Name ?? "-";
                        elemData.TypeName = elemType?.Name ?? "-";
                    }
                    else
                    {
                        elemData.FamilyName = element.GetType().Name;
                        elemData.TypeName = "-";
                    }
                }

                // Get instance parameters
                foreach (Parameter param in element.Parameters)
                {
                    if (!param.HasValue) continue;
                    if (IsInternalParameter(param)) continue;

                    var name = param.Definition.Name;
                    var key = $"{name}|I";

                    if (!paramDefs.ContainsKey(key))
                    {
                        paramDefs[key] = CreateParameterDefinition(param, true);
                    }

                    elemData.Values[key] = CreateParameterValue(doc, param, true);
                }

                // Get type parameters
                var typeId2 = element.GetTypeId();
                if (typeId2 != ElementId.InvalidElementId)
                {
                    var elementType = doc.GetElement(typeId2);
                    if (elementType != null)
                    {
                        foreach (Parameter param in elementType.Parameters)
                        {
                            if (!param.HasValue) continue;
                            if (IsInternalParameter(param)) continue;

                            var name = param.Definition.Name;
                            var key = $"{name}|T";

                            if (!paramDefs.ContainsKey(key))
                            {
                                paramDefs[key] = CreateParameterDefinition(param, false);
                            }

                            elemData.Values[key] = CreateParameterValue(doc, param, false);
                        }
                    }
                }

                elementDataList.Add(elemData);
            }

            // Group parameters
            var groupedParams = GroupParameters(paramDefs.Values.ToList());

            // Invoke callback on UI thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnParametersLoaded?.Invoke(groupedParams, elementDataList);
            });
        }

        private void ExecuteGetParameterValues(Document doc, List<long> elementIds, List<string> paramNames)
        {
            if (paramNames == null || paramNames.Count == 0)
            {
                ExecuteGetAllParameters(doc, elementIds);
                return;
            }

            RaiseStatus("Loading parameter values...");

            var paramFilter = new HashSet<string>(paramNames, StringComparer.Ordinal);
            var paramDefs = new Dictionary<string, ParameterDefinition>();
            var elementDataList = new List<ElementParameterData>();

            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                var elemData = new ElementParameterData
                {
                    ElementId = id,
                    Category = element.Category?.Name ?? "-"
                };

                if (element is FamilyInstance fi && fi.Symbol != null)
                {
                    elemData.FamilyName = fi.Symbol.Family?.Name ?? "-";
                    elemData.TypeName = fi.Symbol.Name ?? "-";
                }
                else
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var elemType = doc.GetElement(typeId);
                        var familyParam = elemType?.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                        elemData.FamilyName = familyParam?.AsString() ?? elemType?.Name ?? "-";
                        elemData.TypeName = elemType?.Name ?? "-";
                    }
                    else
                    {
                        elemData.FamilyName = element.GetType().Name;
                        elemData.TypeName = "-";
                    }
                }

                foreach (Parameter param in element.Parameters)
                {
                    if (!param.HasValue || IsInternalParameter(param)) continue;
                    var name = param.Definition?.Name;
                    if (name == null || !paramFilter.Contains(name)) continue;

                    var key = $"{name}|I";
                    if (!paramDefs.ContainsKey(key))
                        paramDefs[key] = CreateParameterDefinition(param, true);
                    elemData.Values[key] = CreateParameterValue(doc, param, true);
                }

                var typeId2 = element.GetTypeId();
                if (typeId2 != ElementId.InvalidElementId)
                {
                    var elementType = doc.GetElement(typeId2);
                    if (elementType != null)
                    {
                        foreach (Parameter param in elementType.Parameters)
                        {
                            if (!param.HasValue || IsInternalParameter(param)) continue;
                            var name = param.Definition?.Name;
                            if (name == null || !paramFilter.Contains(name)) continue;

                            var key = $"{name}|T";
                            if (!paramDefs.ContainsKey(key))
                                paramDefs[key] = CreateParameterDefinition(param, false);
                            elemData.Values[key] = CreateParameterValue(doc, param, false);
                        }
                    }
                }

                elementDataList.Add(elemData);
            }

            var groupedParams = GroupParameters(paramDefs.Values.ToList());

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnParametersLoaded?.Invoke(groupedParams, elementDataList);
            });
        }

        #endregion

        #region Group 2: Editor

        private void ExecuteBatchUpdate(Document doc, ParameterUpdateBatch batch)
        {
            if (batch?.Updates == null || batch.Updates.Count == 0)
            {
                RaiseError("No updates to apply");
                return;
            }

            RaiseStatus($"Updating {batch.Updates.Count} parameters...");

            var result = new UpdateResult();
            var errors = new List<string>();
            var warnings = new List<string>();

            using (var trans = new Transaction(doc, "Batch Update Parameters"))
            {
                trans.Start();

                // Group type parameter updates to avoid duplicate writes
                var typeUpdates = new Dictionary<(ElementId typeId, string paramName), ParameterBatchUpdateItem>();

                foreach (var update in batch.Updates)
                {
                    try
                    {
                        var elemId = new ElementId(update.ElementId);
                        var element = doc.GetElement(elemId);
                        if (element == null)
                        {
                            errors.Add($"Element {update.ElementId} not found");
                            result.FailedCount++;
                            continue;
                        }

                        Parameter param = null;
                        
                        if (update.IsInstance)
                        {
                            param = element.LookupParameter(update.ParameterName);
                        }
                        else
                        {
                            // Type parameter - check if already processed
                            var typeId = element.GetTypeId();
                            if (typeId == ElementId.InvalidElementId)
                            {
                                errors.Add($"Element {update.ElementId} has no type");
                                result.FailedCount++;
                                continue;
                            }

                            var key = (typeId, update.ParameterName);
                            if (typeUpdates.ContainsKey(key))
                            {
                                result.SkippedCount++;
                                continue;
                            }
                            typeUpdates[key] = update;

                            var elemType = doc.GetElement(typeId);
                            param = elemType?.LookupParameter(update.ParameterName);
                        }

                        if (param == null)
                        {
                            errors.Add($"Parameter '{update.ParameterName}' not found on element {update.ElementId}");
                            result.FailedCount++;
                            continue;
                        }

                        if (param.IsReadOnly)
                        {
                            warnings.Add($"Parameter '{update.ParameterName}' is read-only");
                            result.SkippedCount++;
                            continue;
                        }

                        // Apply value based on mode
                        string newValue = update.NewValue;
                        if (batch.Mode == UpdateMode.Formula && !string.IsNullOrEmpty(batch.Formula))
                        {
                            var storageType = ConvertStorageType(param.StorageType);
                            newValue = FormulaParser.ApplyFormula(batch.Formula, update.OldValue, storageType);
                        }

                        if (SetParameterValue(param, newValue, out string error))
                        {
                            result.SuccessCount++;
                        }
                        else
                        {
                            errors.Add($"Failed to set '{update.ParameterName}' on {update.ElementId}: {error}");
                            result.FailedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error updating {update.ParameterName} on {update.ElementId}: {ex.Message}");
                        result.FailedCount++;
                    }
                }

                trans.Commit();
            }

            result.Errors = errors;
            result.Warnings = warnings;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnUpdateCompleted?.Invoke(result);
            });
        }

        private void ExecuteApplyFormula(Document doc, List<long> elementIds, string paramName, 
            bool isInstance, string formula)
        {
            RaiseStatus($"Applying formula to {elementIds.Count} elements...");

            var updates = new List<ParameterBatchUpdateItem>();

            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                Parameter param;
                if (isInstance)
                {
                    param = element.LookupParameter(paramName);
                }
                else
                {
                    var typeId = element.GetTypeId();
                    var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    param = elemType?.LookupParameter(paramName);
                }

                if (param == null || param.IsReadOnly) continue;

                var oldValue = GetParameterValueAsString(doc, param);
                var storageType = ConvertStorageType(param.StorageType);
                var newValue = FormulaParser.ApplyFormula(formula, oldValue, storageType);

                updates.Add(new ParameterBatchUpdateItem
                {
                    ElementId = id,
                    ParameterName = paramName,
                    IsInstance = isInstance,
                    OldValue = oldValue,
                    NewValue = newValue
                });
            }

            var batch = new ParameterUpdateBatch
            {
                Updates = updates,
                Mode = UpdateMode.Direct
            };

            ExecuteBatchUpdate(doc, batch);
        }

        #endregion

        #region Group 3: Transfer

        private void ExecuteMapParameters(Document doc, List<long> elementIds, string sourceParam, 
            bool sourceIsInstance, string targetParam, bool targetIsInstance)
        {
            RaiseStatus($"Mapping {sourceParam} → {targetParam}...");

            var updates = new List<ParameterBatchUpdateItem>();

            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                // Get source value
                Parameter srcParam;
                if (sourceIsInstance)
                {
                    srcParam = element.LookupParameter(sourceParam);
                }
                else
                {
                    var typeId = element.GetTypeId();
                    var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    srcParam = elemType?.LookupParameter(sourceParam);
                }

                if (srcParam == null) continue;
                var sourceValue = GetParameterValueAsString(doc, srcParam);

                // Get target parameter
                Parameter tgtParam;
                if (targetIsInstance)
                {
                    tgtParam = element.LookupParameter(targetParam);
                }
                else
                {
                    var typeId = element.GetTypeId();
                    var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    tgtParam = elemType?.LookupParameter(targetParam);
                }

                if (tgtParam == null || tgtParam.IsReadOnly) continue;

                updates.Add(new ParameterBatchUpdateItem
                {
                    ElementId = id,
                    ParameterName = targetParam,
                    IsInstance = targetIsInstance,
                    OldValue = GetParameterValueAsString(doc, tgtParam),
                    NewValue = sourceValue
                });
            }

            var batch = new ParameterUpdateBatch { Updates = updates, Mode = UpdateMode.Direct };
            ExecuteBatchUpdate(doc, batch);
        }

        private void ExecuteTransferBetweenElements(Document doc, long sourceElementId, 
            List<long> targetElementIds, List<string> paramNames)
        {
            RaiseStatus($"Transferring from element {sourceElementId}...");

            var srcElemId = new ElementId(sourceElementId);
            var srcElement = doc.GetElement(srcElemId);
            if (srcElement == null)
            {
                RaiseError("Source element not found");
                return;
            }

            // Get source values
            var sourceValues = new Dictionary<string, (string value, bool isInstance)>();
            foreach (var paramName in paramNames)
            {
                // Try instance first
                var param = srcElement.LookupParameter(paramName);
                if (param != null && param.HasValue)
                {
                    sourceValues[paramName] = (GetParameterValueAsString(doc, param), true);
                    continue;
                }

                // Try type
                var typeId = srcElement.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var elemType = doc.GetElement(typeId);
                    param = elemType?.LookupParameter(paramName);
                    if (param != null && param.HasValue)
                    {
                        sourceValues[paramName] = (GetParameterValueAsString(doc, param), false);
                    }
                }
            }

            // Build updates for target elements
            var updates = new List<ParameterBatchUpdateItem>();
            foreach (var targetId in targetElementIds)
            {
                if (targetId == sourceElementId) continue;

                foreach (var kvp in sourceValues)
                {
                    updates.Add(new ParameterBatchUpdateItem
                    {
                        ElementId = targetId,
                        ParameterName = kvp.Key,
                        IsInstance = kvp.Value.isInstance,
                        NewValue = kvp.Value.value
                    });
                }
            }

            var batch = new ParameterUpdateBatch { Updates = updates, Mode = UpdateMode.Direct };
            ExecuteBatchUpdate(doc, batch);
        }

        private void ExecuteExportToCsv(Document doc, List<long> elementIds, List<string> paramNames, string filePath)
        {
            RaiseStatus("Exporting to CSV...");

            var sb = new StringBuilder();

            // Header
            sb.Append("ElementId,Family,Type,Category");
            foreach (var paramName in paramNames)
            {
                sb.Append($",\"{EscapeCsv(paramName)}\"");
            }
            sb.AppendLine();

            // Data rows
            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                string familyName = "-", typeName = "-", category = element.Category?.Name ?? "-";

                if (element is FamilyInstance fi && fi.Symbol != null)
                {
                    familyName = fi.Symbol.Family?.Name ?? "-";
                    typeName = fi.Symbol.Name ?? "-";
                }
                else
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var elemType = doc.GetElement(typeId);
                        var familyParam = elemType?.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                        familyName = familyParam?.AsString() ?? elemType?.Name ?? "-";
                        typeName = elemType?.Name ?? "-";
                    }
                }

                sb.Append($"{id},\"{EscapeCsv(familyName)}\",\"{EscapeCsv(typeName)}\",\"{EscapeCsv(category)}\"");

                foreach (var paramName in paramNames)
                {
                    var value = GetParameterValue(doc, element, paramName);
                    sb.Append($",\"{EscapeCsv(value)}\"");
                }
                sb.AppendLine();
            }

            try
            {
                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
                RaiseStatus($"Exported {elementIds.Count} elements to CSV");
            }
            catch (Exception ex)
            {
                RaiseError($"Failed to export: {ex.Message}");
            }
        }

        #endregion

        #region Group 4: Comparison

        private void ExecuteCompareTwoElements(Document doc, long elementId1, long elementId2)
        {
            RaiseStatus("Comparing elements...");

            var elem1 = doc.GetElement(new ElementId(elementId1));
            var elem2 = doc.GetElement(new ElementId(elementId2));

            if (elem1 == null || elem2 == null)
            {
                RaiseError("One or both elements not found");
                return;
            }

            var result = new ComparisonResult
            {
                ElementId1 = elementId1,
                ElementId2 = elementId2,
                Element1Name = GetElementName(doc, elem1),
                Element2Name = GetElementName(doc, elem2)
            };

            // Get all parameters from both elements
            var params1 = GetAllParameterValues(doc, elem1);
            var params2 = GetAllParameterValues(doc, elem2);

            var allKeys = params1.Keys.Union(params2.Keys).OrderBy(k => k);

            foreach (var key in allKeys)
            {
                var hasP1 = params1.TryGetValue(key, out var v1);
                var hasP2 = params2.TryGetValue(key, out var v2);

                var parts = key.Split('|');
                var paramName = parts[0];
                var isInstance = parts.Length > 1 && parts[1] == "I";

                if (!hasP1 && hasP2)
                {
                    result.Differences.Add(new ParameterDifference
                    {
                        ParameterName = paramName,
                        IsInstance = isInstance,
                        Value1 = "-",
                        Value2 = v2,
                        Type = DifferenceType.MissingInFirst
                    });
                }
                else if (hasP1 && !hasP2)
                {
                    result.Differences.Add(new ParameterDifference
                    {
                        ParameterName = paramName,
                        IsInstance = isInstance,
                        Value1 = v1,
                        Value2 = "-",
                        Type = DifferenceType.MissingInSecond
                    });
                }
                else if (v1 != v2)
                {
                    result.Differences.Add(new ParameterDifference
                    {
                        ParameterName = paramName,
                        IsInstance = isInstance,
                        Value1 = v1,
                        Value2 = v2,
                        Type = DifferenceType.Different
                    });
                }
                else
                {
                    result.MatchCount++;
                }
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnComparisonCompleted?.Invoke(result);
            });
        }

        private void ExecuteFindEmptyValues(Document doc, List<long> elementIds, List<string> paramNames)
        {
            RaiseStatus("Finding empty values...");

            var emptyList = new List<EmptyParameterInfo>();

            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                var elemName = GetElementName(doc, element);

                foreach (var paramName in paramNames)
                {
                    // Check instance parameter
                    var param = element.LookupParameter(paramName);
                    if (param != null)
                    {
                        var value = GetParameterValueAsString(doc, param);
                        if (IsEmptyValue(value))
                        {
                            emptyList.Add(new EmptyParameterInfo
                            {
                                ElementId = id,
                                ElementName = elemName,
                                ParameterName = paramName,
                                IsInstance = true
                            });
                        }
                        continue;
                    }

                    // Check type parameter
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var elemType = doc.GetElement(typeId);
                        param = elemType?.LookupParameter(paramName);
                        if (param != null)
                        {
                            var value = GetParameterValueAsString(doc, param);
                            if (IsEmptyValue(value))
                            {
                                emptyList.Add(new EmptyParameterInfo
                                {
                                    ElementId = id,
                                    ElementName = elemName,
                                    ParameterName = paramName,
                                    IsInstance = false
                                });
                            }
                        }
                    }
                }
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnEmptyValuesFound?.Invoke(emptyList);
            });
        }

        private void ExecuteFindDuplicates(Document doc, List<long> elementIds, string paramName, bool isInstance)
        {
            RaiseStatus("Finding duplicates...");

            var valueGroups = new Dictionary<string, List<long>>();

            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                Parameter param;
                if (isInstance)
                {
                    param = element.LookupParameter(paramName);
                }
                else
                {
                    var typeId = element.GetTypeId();
                    var elemType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    param = elemType?.LookupParameter(paramName);
                }

                if (param == null) continue;

                var value = GetParameterValueAsString(doc, param);
                if (string.IsNullOrEmpty(value) || value == "-" || value == "<none>") continue;

                if (!valueGroups.ContainsKey(value))
                {
                    valueGroups[value] = new List<long>();
                }
                valueGroups[value].Add(id);
            }

            var duplicates = valueGroups
                .Where(kvp => kvp.Value.Count > 1)
                .Select(kvp => new DuplicateGroup
                {
                    ParameterName = paramName,
                    Value = kvp.Key,
                    ElementIds = kvp.Value
                })
                .OrderByDescending(d => d.Count)
                .ToList();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnDuplicatesFound?.Invoke(duplicates);
            });
        }

        #endregion

        #region Group 5: Audit

        private void ExecuteValidateParameters(Document doc, List<long> elementIds, List<ValidationRule> rules)
        {
            RaiseStatus("Validating parameters...");

            var report = new ValidationReport
            {
                TotalElements = elementIds.Count
            };

            var elementsWithIssues = new HashSet<long>();

            // For unique validation, collect all values first
            var uniqueValues = new Dictionary<string, Dictionary<string, List<long>>>();
            foreach (var rule in rules.Where(r => r.IsEnabled && r.Type == ValidationType.Unique))
            {
                uniqueValues[rule.ParameterName] = new Dictionary<string, List<long>>();
            }

            // First pass: collect values for unique check
            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                foreach (var rule in rules.Where(r => r.IsEnabled && r.Type == ValidationType.Unique))
                {
                    var value = GetParameterValue(doc, element, rule.ParameterName);
                    if (!string.IsNullOrEmpty(value) && value != "-")
                    {
                        if (!uniqueValues[rule.ParameterName].ContainsKey(value))
                        {
                            uniqueValues[rule.ParameterName][value] = new List<long>();
                        }
                        uniqueValues[rule.ParameterName][value].Add(id);
                    }
                }
            }

            // Second pass: validate all rules
            foreach (var id in elementIds)
            {
                var elemId = new ElementId(id);
                var element = doc.GetElement(elemId);
                if (element == null) continue;

                var elemName = GetElementName(doc, element);

                foreach (var rule in rules.Where(r => r.IsEnabled))
                {
                    var value = GetParameterValue(doc, element, rule.ParameterName);
                    string issueMessage = null;

                    switch (rule.Type)
                    {
                        case ValidationType.NotEmpty:
                            if (IsEmptyValue(value))
                            {
                                issueMessage = rule.Message ?? $"Parameter '{rule.ParameterName}' is empty";
                            }
                            break;

                        case ValidationType.Format:
                            if (!string.IsNullOrEmpty(rule.Pattern) && !IsEmptyValue(value))
                            {
                                if (!Regex.IsMatch(value, rule.Pattern))
                                {
                                    issueMessage = rule.Message ?? $"Value '{value}' does not match pattern '{rule.Pattern}'";
                                }
                            }
                            break;

                        case ValidationType.Range:
                            if (!IsEmptyValue(value) && double.TryParse(value, out double numVal))
                            {
                                if (rule.MinValue.HasValue && numVal < rule.MinValue.Value)
                                {
                                    issueMessage = rule.Message ?? $"Value {numVal} is below minimum {rule.MinValue}";
                                }
                                else if (rule.MaxValue.HasValue && numVal > rule.MaxValue.Value)
                                {
                                    issueMessage = rule.Message ?? $"Value {numVal} is above maximum {rule.MaxValue}";
                                }
                            }
                            break;

                        case ValidationType.AllowedList:
                            if (!IsEmptyValue(value) && rule.AllowedValues?.Count > 0)
                            {
                                if (!rule.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                                {
                                    issueMessage = rule.Message ?? $"Value '{value}' is not in allowed list";
                                }
                            }
                            break;

                        case ValidationType.Unique:
                            if (!IsEmptyValue(value) && uniqueValues.TryGetValue(rule.ParameterName, out var valDict))
                            {
                                if (valDict.TryGetValue(value, out var ids) && ids.Count > 1)
                                {
                                    issueMessage = rule.Message ?? $"Duplicate value '{value}' found in {ids.Count} elements";
                                }
                            }
                            break;
                    }

                    if (issueMessage != null)
                    {
                        elementsWithIssues.Add(id);
                        report.Issues.Add(new ValidationIssue
                        {
                            ElementId = id,
                            ElementName = elemName,
                            ParameterName = rule.ParameterName,
                            IsInstance = rule.IsInstance,
                            CurrentValue = value,
                            RuleId = rule.Id,
                            RuleType = rule.Type,
                            Severity = rule.Severity,
                            Message = issueMessage
                        });

                        switch (rule.Severity)
                        {
                            case ValidationSeverity.Error: report.ErrorCount++; break;
                            case ValidationSeverity.Warning: report.WarningCount++; break;
                            case ValidationSeverity.Info: report.InfoCount++; break;
                        }
                    }
                }
            }

            report.ElementsWithIssues = elementsWithIssues.Count;

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnValidationCompleted?.Invoke(report);
            });
        }

        private void ExecuteSelectElements(UIDocument uidoc, List<long> elementIds)
        {
            if (elementIds == null || elementIds.Count == 0) return;

            var ids = elementIds.Select(id => new ElementId(id)).ToList();
            uidoc.Selection.SetElementIds(ids);
            RaiseStatus($"Selected {ids.Count} elements");
        }

        #endregion

        #region Helper Methods

        private ParameterDefinition CreateParameterDefinition(Parameter param, bool isInstance)
        {
            var def = param.Definition;
            var internalDef = def as InternalDefinition;

            return new ParameterDefinition
            {
                Name = def.Name,
                IsInstance = isInstance,
                IsReadOnly = param.IsReadOnly || (internalDef?.BuiltInParameter != BuiltInParameter.INVALID && param.IsReadOnly),
                IsBuiltIn = internalDef?.BuiltInParameter != BuiltInParameter.INVALID,
                IsShared = param.IsShared,
                StorageType = ConvertStorageType(param.StorageType),
                GroupType = ConvertParameterGroup(def.GetGroupTypeId()),
                GroupName = GetGroupName(def.GetGroupTypeId())
            };
        }

        private Models.ParameterValue CreateParameterValue(Document doc, Parameter param, bool isInstance)
        {
            var value = GetParameterValueAsString(doc, param);
            return new Models.ParameterValue
            {
                ParameterName = param.Definition.Name,
                IsInstance = isInstance,
                StorageType = ConvertStorageType(param.StorageType),
                IsReadOnly = param.IsReadOnly,
                IsNull = !param.HasValue,
                StringValue = value,
                OriginalValue = value
            };
        }

        private List<ParameterGroupNode> GroupParameters(List<ParameterDefinition> paramDefs)
        {
            var result = new List<ParameterGroupNode>();

            // Group by Instance/Type first, then by GroupType
            var instanceParams = paramDefs.Where(p => p.IsInstance).GroupBy(p => p.GroupType);
            var typeParams = paramDefs.Where(p => !p.IsInstance).GroupBy(p => p.GroupType);

            foreach (var group in instanceParams)
            {
                result.Add(new ParameterGroupNode
                {
                    GroupName = "Instance",
                    SubGroupName = group.Key.ToString(),
                    Parameters = group.OrderBy(p => p.Name).ToList()
                });
            }

            foreach (var group in typeParams)
            {
                result.Add(new ParameterGroupNode
                {
                    GroupName = "Type",
                    SubGroupName = group.Key.ToString(),
                    Parameters = group.OrderBy(p => p.Name).ToList()
                });
            }

            return result.OrderBy(g => g.GroupName).ThenBy(g => g.SubGroupName).ToList();
        }

        private ParamStorageType ConvertStorageType(StorageType st)
        {
            return st switch
            {
                StorageType.String => ParamStorageType.String,
                StorageType.Integer => ParamStorageType.Integer,
                StorageType.Double => ParamStorageType.Double,
                StorageType.ElementId => ParamStorageType.ElementId,
                _ => ParamStorageType.None
            };
        }

        private ParamGroupType ConvertParameterGroup(ForgeTypeId groupTypeId)
        {
            if (groupTypeId == null) return ParamGroupType.Other;

            var typeName = groupTypeId.TypeId ?? "";

            if (typeName.Contains("text", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Text;
            if (typeName.Contains("dimension", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Dimensions;
            if (typeName.Contains("identity", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.IdentityData;
            if (typeName.Contains("constraint", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Constraints;
            if (typeName.Contains("graphic", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Graphics;
            if (typeName.Contains("material", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Materials;
            if (typeName.Contains("phasing", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Phasing;
            if (typeName.Contains("structural", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Structural;
            if (typeName.Contains("mechanical", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Mechanical;
            if (typeName.Contains("electrical", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Electrical;
            if (typeName.Contains("plumb", StringComparison.OrdinalIgnoreCase)) return ParamGroupType.Plumbing;

            return ParamGroupType.Other;
        }

        private string GetGroupName(ForgeTypeId groupTypeId)
        {
            if (groupTypeId == null) return "Other";
            return groupTypeId.TypeId?.Split('-').LastOrDefault()?.Replace("autodesk.spec.aec:", "") ?? "Other";
        }

        private bool IsInternalParameter(Parameter param)
        {
            var name = param.Definition?.Name;
            if (string.IsNullOrEmpty(name)) return true;

            // Skip internal/system parameters
            if (name.StartsWith("ELEM_") || name.StartsWith("HOST_") || name.StartsWith("PHASE_"))
                return true;

            return false;
        }

        private string GetParameterValueAsString(Document doc, Parameter param)
        {
            if (param == null || !param.HasValue) return "";

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "";

                case StorageType.Integer:
                    // Check for Yes/No
                    var paramDef = param.Definition;
                    if (paramDef != null)
                    {
                        var specTypeId = paramDef.GetDataType();
                        if (specTypeId == SpecTypeId.Boolean.YesNo)
                        {
                            return param.AsInteger() == 1 ? "Yes" : "No";
                        }
                    }
                    return param.AsInteger().ToString();

                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F4");

                case StorageType.ElementId:
                    var elemId = param.AsElementId();
                    if (elemId == ElementId.InvalidElementId) return "<none>";
                    var elem = doc.GetElement(elemId);
                    return elem?.Name ?? elemId.Value.ToString();

                default:
                    return "";
            }
        }

        private string GetParameterValue(Document doc, Element element, string paramName)
        {
            // Try instance first
            var param = element.LookupParameter(paramName);
            if (param != null && param.HasValue)
            {
                return GetParameterValueAsString(doc, param);
            }

            // Try type
            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                param = elemType?.LookupParameter(paramName);
                if (param != null && param.HasValue)
                {
                    return GetParameterValueAsString(doc, param);
                }
            }

            return "-";
        }

        private Dictionary<string, string> GetAllParameterValues(Document doc, Element element)
        {
            var result = new Dictionary<string, string>();

            // Instance parameters
            foreach (Parameter param in element.Parameters)
            {
                if (!param.HasValue) continue;
                if (IsInternalParameter(param)) continue;
                var key = $"{param.Definition.Name}|I";
                result[key] = GetParameterValueAsString(doc, param);
            }

            // Type parameters
            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    foreach (Parameter param in elemType.Parameters)
                    {
                        if (!param.HasValue) continue;
                        if (IsInternalParameter(param)) continue;
                        var key = $"{param.Definition.Name}|T";
                        result[key] = GetParameterValueAsString(doc, param);
                    }
                }
            }

            return result;
        }

        private string GetElementName(Document doc, Element element)
        {
            if (element is FamilyInstance fi && fi.Symbol != null)
            {
                return $"{fi.Symbol.Family?.Name} : {fi.Symbol.Name}";
            }

            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                return elemType?.Name ?? element.Name ?? element.Id.Value.ToString();
            }

            return element.Name ?? element.Id.Value.ToString();
        }

        private bool IsEmptyValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value == "-" || value == "<none>" || value == "<null>";
        }

        private bool SetParameterValue(Parameter param, string newValue, out string error)
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
                        param.Set(newValue ?? "");
                        return true;

                    case StorageType.Integer:
                        // Check for Yes/No
                        var paramDef = param.Definition;
                        if (paramDef != null)
                        {
                            var specTypeId = paramDef.GetDataType();
                            if (specTypeId == SpecTypeId.Boolean.YesNo)
                            {
                                var lower = newValue?.ToLower() ?? "";
                                if (lower == "yes" || lower == "1" || lower == "true")
                                {
                                    param.Set(1);
                                    return true;
                                }
                                if (lower == "no" || lower == "0" || lower == "false")
                                {
                                    param.Set(0);
                                    return true;
                                }
                                error = "Yes/No parameter requires: Yes, No, 1, 0, True, or False";
                                return false;
                            }
                        }
                        if (int.TryParse(newValue, out int intVal))
                        {
                            param.Set(intVal);
                            return true;
                        }
                        error = "Invalid integer value";
                        return false;

                    case StorageType.Double:
                        if (double.TryParse(newValue, out double dblVal))
                        {
                            param.Set(dblVal);
                            return true;
                        }
                        error = "Invalid numeric value";
                        return false;

                    case StorageType.ElementId:
                        if (long.TryParse(newValue, out long idVal))
                        {
                            param.Set(new ElementId(idVal));
                            return true;
                        }
                        error = "Invalid Element ID";
                        return false;

                    default:
                        error = "Unknown storage type";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\"", "\"\"");
        }

        private void RaiseError(string message)
        {
            Application.Current?.Dispatcher.Invoke(() => OnError?.Invoke(message));
        }

        private void RaiseStatus(string message)
        {
            Application.Current?.Dispatcher.Invoke(() => OnStatusUpdate?.Invoke(message));
        }

        #endregion
    }
}
