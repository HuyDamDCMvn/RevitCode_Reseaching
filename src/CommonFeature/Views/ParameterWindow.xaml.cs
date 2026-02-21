using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.UI;
using CommonFeature.Handlers;
using CommonFeature.Models;
using Microsoft.Win32;

namespace CommonFeature.Views
{
    /// <summary>
    /// Code-behind for ParameterWindow.xaml
    /// Implements Parameter Manager with 5 feature groups:
    /// 1. Browse - View and explore parameters
    /// 2. Edit - Modify parameter values with formulas
    /// 3. Transfer - Map parameters and import/export CSV
    /// 4. Compare - Compare elements and find issues
    /// 5. Validate - QA/QC with validation rules
    /// </summary>
    public partial class ParameterWindow : Window
    {
        #region Fields

        private ParameterExternalHandler _handler;
        private ExternalEvent _externalEvent;

        // Data
        private List<ParameterGroupNode> _parameterGroups = new();
        private List<ElementParameterData> _elementDataList = new();
        private List<ParameterDefinition> _allParameters = new();
        private List<ParameterDefinition> _selectedParameters = new();
        
        // Edit tracking
        private List<ParameterBatchUpdateItem> _pendingUpdates = new();
        private Stack<ParameterBatchUpdateItem> _undoStack = new();

        // Validation
        private List<Models.ValidationRule> _validationRules = new();
        private Models.ValidationReport _currentReport;

        // Comparison
        private ComparisonResult _currentComparison;
        private List<EmptyParameterInfo> _emptyValues = new();
        private List<DuplicateGroup> _duplicates = new();

        // Callbacks for getting current selection from Revit
        public Func<List<long>> GetCurrentSelectionCallback { get; set; }

        #endregion

        #region Constructor & Lifecycle

        public ParameterWindow()
        {
            try
            {
                InitializeComponent();
                Closing += ParameterWindow_Closing;
                Loaded += ParameterWindow_Loaded;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to initialize Parameter Window:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void ParameterWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Initialize after UI is fully loaded
            InitializeDefaultValidationRules();
        }

        public void Initialize(ParameterExternalHandler handler, ExternalEvent externalEvent)
        {
            try
            {
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));

                // Subscribe to handler events
                _handler.OnParametersLoaded += Handler_OnParametersLoaded;
                _handler.OnUpdateCompleted += Handler_OnUpdateCompleted;
                _handler.OnComparisonCompleted += Handler_OnComparisonCompleted;
                _handler.OnEmptyValuesFound += Handler_OnEmptyValuesFound;
                _handler.OnDuplicatesFound += Handler_OnDuplicatesFound;
                _handler.OnValidationCompleted += Handler_OnValidationCompleted;
                _handler.OnError += Handler_OnError;
                _handler.OnStatusUpdate += Handler_OnStatusUpdate;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to initialize handler:\n\n{ex.Message}",
                    "Handler Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void ParameterWindow_Closing(object sender, CancelEventArgs e)
        {
            // Unsubscribe events
            if (_handler != null)
            {
                _handler.OnParametersLoaded -= Handler_OnParametersLoaded;
                _handler.OnUpdateCompleted -= Handler_OnUpdateCompleted;
                _handler.OnComparisonCompleted -= Handler_OnComparisonCompleted;
                _handler.OnEmptyValuesFound -= Handler_OnEmptyValuesFound;
                _handler.OnDuplicatesFound -= Handler_OnDuplicatesFound;
                _handler.OnValidationCompleted -= Handler_OnValidationCompleted;
                _handler.OnError -= Handler_OnError;
                _handler.OnStatusUpdate -= Handler_OnStatusUpdate;
            }
        }

        private void InitializeDefaultValidationRules()
        {
            _validationRules = new List<Models.ValidationRule>
            {
                new Models.ValidationRule
                {
                    ParameterName = "Mark",
                    IsInstance = true,
                    Type = ValidationType.NotEmpty,
                    Severity = ValidationSeverity.Warning,
                    Message = "Mark should not be empty"
                },
                new Models.ValidationRule
                {
                    ParameterName = "Comments",
                    IsInstance = true,
                    Type = ValidationType.NotEmpty,
                    Severity = ValidationSeverity.Info,
                    Message = "Comments is empty"
                }
            };
            
            ValidationRulesList.ItemsSource = _validationRules;
        }

        #endregion

        #region Handler Event Callbacks

        private void Handler_OnParametersLoaded(List<ParameterGroupNode> groups, List<ElementParameterData> elements)
        {
            Dispatcher.Invoke(() =>
            {
                _parameterGroups = groups;
                _elementDataList = elements;

                // Flatten parameters for combos
                _allParameters = groups.SelectMany(g => g.Parameters).ToList();

                // Update UI
                ParameterTreeView.ItemsSource = groups;
                MainDataGrid.ItemsSource = elements;
                EditDataGrid.ItemsSource = elements;

                // Update combos
                UpdateParameterCombos();

                // Update header
                HeaderSubtitle.Text = $"{elements.Count} elements loaded, {_allParameters.Count} parameters found";
                SetStatus($"Loaded {elements.Count} elements with {_allParameters.Count} parameters");

                HideLoading();
            });
        }

        private void Handler_OnUpdateCompleted(UpdateResult result)
        {
            Dispatcher.Invoke(() =>
            {
                var message = result.Summary;
                if (result.HasErrors)
                {
                    message += $"\nErrors: {string.Join("\n", result.Errors.Take(5))}";
                    if (result.Errors.Count > 5) message += $"\n...and {result.Errors.Count - 5} more errors";
                }

                MessageBox.Show(message, "Update Result", MessageBoxButton.OK,
                    result.HasErrors ? MessageBoxImage.Warning : MessageBoxImage.Information);

                // Clear pending updates
                _pendingUpdates.Clear();
                UpdateEditModifiedCount();

                // Refresh data
                RefreshData();

                HideLoading();
            });
        }

        private void Handler_OnComparisonCompleted(ComparisonResult result)
        {
            Dispatcher.Invoke(() =>
            {
                _currentComparison = result;
                CompareDataGrid.ItemsSource = result.Differences;
                CompareResultHeader.Text = $"Comparison: {result.Element1Name} vs {result.Element2Name}";
                CompareResultSummary.Text = result.Summary;

                HideLoading();
            });
        }

        private void Handler_OnEmptyValuesFound(List<EmptyParameterInfo> emptyList)
        {
            Dispatcher.Invoke(() =>
            {
                _emptyValues = emptyList;

                // Convert to differences for display
                var diffs = emptyList.Select(e => new ParameterDifference
                {
                    ParameterName = e.ParameterName,
                    IsInstance = e.IsInstance,
                    Value1 = e.ElementId.ToString(),
                    Value2 = e.ElementName,
                    Type = DifferenceType.Empty
                }).ToList();

                CompareDataGrid.ItemsSource = diffs;
                CompareResultHeader.Text = "Empty Values Found";
                CompareResultSummary.Text = $"{emptyList.Count} empty parameter values in {emptyList.Select(e => e.ElementId).Distinct().Count()} elements";

                HideLoading();
            });
        }

        private void Handler_OnDuplicatesFound(List<DuplicateGroup> duplicates)
        {
            Dispatcher.Invoke(() =>
            {
                _duplicates = duplicates;

                // Convert to differences for display
                var diffs = duplicates.SelectMany(d => d.ElementIds.Select(id => new ParameterDifference
                {
                    ParameterName = d.ParameterName,
                    Value1 = id.ToString(),
                    Value2 = d.Value,
                    Type = DifferenceType.Different
                })).ToList();

                CompareDataGrid.ItemsSource = diffs;
                CompareResultHeader.Text = "Duplicate Values Found";
                CompareResultSummary.Text = $"{duplicates.Count} duplicate groups, {duplicates.Sum(d => d.Count)} total elements";

                HideLoading();
            });
        }

        private void Handler_OnValidationCompleted(ValidationReport report)
        {
            Dispatcher.Invoke(() =>
            {
                _currentReport = report;
                ValidationDataGrid.ItemsSource = report.Issues;
                ValidationReportHeader.Text = "Validation Report";
                ValidationReportSummary.Text = report.Summary;
                ErrorCountText.Text = $"🔴 {report.ErrorCount}";
                WarningCountText.Text = $"🟡 {report.WarningCount}";

                HideLoading();
            });
        }

        private void Handler_OnError(string message)
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus($"Error: {message}");
                MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                HideLoading();
            });
        }

        private void Handler_OnStatusUpdate(string message)
        {
            Dispatcher.Invoke(() => SetStatus(message));
        }

        #endregion

        #region Tab Navigation

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            // Guard: Skip if window not fully loaded (event fires during XAML initialization)
            if (!IsLoaded) return;
            
            if (sender is RadioButton rb)
            {
                // Hide all panels
                if (BrowsePanel != null) BrowsePanel.Visibility = Visibility.Collapsed;
                if (EditPanel != null) EditPanel.Visibility = Visibility.Collapsed;
                if (TransferPanel != null) TransferPanel.Visibility = Visibility.Collapsed;
                if (ComparePanel != null) ComparePanel.Visibility = Visibility.Collapsed;
                if (ValidatePanel != null) ValidatePanel.Visibility = Visibility.Collapsed;

                // Show selected panel
                if (rb == TabBrowse && BrowsePanel != null) BrowsePanel.Visibility = Visibility.Visible;
                else if (rb == TabEdit && EditPanel != null) EditPanel.Visibility = Visibility.Visible;
                else if (rb == TabTransfer && TransferPanel != null) TransferPanel.Visibility = Visibility.Visible;
                else if (rb == TabCompare && ComparePanel != null) ComparePanel.Visibility = Visibility.Visible;
                else if (rb == TabValidate && ValidatePanel != null) ValidatePanel.Visibility = Visibility.Visible;
            }
        }

        #endregion

        #region Browse Tab

        private void ParamSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = ParamSearchBox.Text?.ToLower() ?? "";
            
            if (string.IsNullOrEmpty(searchText))
            {
                ParameterTreeView.ItemsSource = _parameterGroups;
            }
            else
            {
                var filtered = _parameterGroups.Select(g => new ParameterGroupNode
                {
                    GroupName = g.GroupName,
                    SubGroupName = g.SubGroupName,
                    Parameters = g.Parameters.Where(p => p.Name.ToLower().Contains(searchText)).ToList()
                }).Where(g => g.Parameters.Count > 0).ToList();

                ParameterTreeView.ItemsSource = filtered;
            }
        }

        private void ParameterTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Handle parameter selection in tree
        }

        private void ParamCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Update selected parameters list
            _selectedParameters = _allParameters.Where(p => p.IsSelected).ToList();
        }

        private void AddColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedParameters.Count == 0)
            {
                MessageBox.Show("Please select parameters from the tree first.", "No Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Add columns to DataGrid
            foreach (var param in _selectedParameters)
            {
                var key = param.UniqueKey;
                var header = $"{param.Name} {param.TypeLabel}";

                // Check if column already exists
                var existing = MainDataGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == header);
                if (existing != null) continue;

                var column = new DataGridTextColumn
                {
                    Header = header,
                    Width = new DataGridLength(120),
                    Binding = new Binding($"Values[{key}].DisplayValue")
                };

                MainDataGrid.Columns.Add(column);
            }

            SetStatus($"Added {_selectedParameters.Count} columns");
        }

        private void ClearColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            // Keep only fixed columns (first 4)
            while (MainDataGrid.Columns.Count > 4)
            {
                MainDataGrid.Columns.RemoveAt(MainDataGrid.Columns.Count - 1);
            }

            SetStatus("Cleared parameter columns");
        }

        private void CopyValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainDataGrid.SelectedCells.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var cell in MainDataGrid.SelectedCells)
            {
                if (cell.Item is ElementParameterData data)
                {
                    var column = cell.Column;
                    var binding = (column as DataGridBoundColumn)?.Binding as Binding;
                    if (binding != null)
                    {
                        var path = binding.Path.Path;
                        if (path.StartsWith("Values[") && path.IndexOf(']') > 7)
                        {
                            var key = path.Substring(7, path.IndexOf(']') - 7);
                            if (data.Values.TryGetValue(key, out var pv))
                            {
                                sb.AppendLine(pv.DisplayValue);
                            }
                        }
                        else
                        {
                            // Fixed column
                            var prop = typeof(ElementParameterData).GetProperty(path);
                            var value = prop?.GetValue(data)?.ToString() ?? "";
                            sb.AppendLine(value);
                        }
                    }
                }
            }

            if (sb.Length > 0)
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
                SetStatus("Copied to clipboard");
            }
        }

        #endregion

        #region Edit Tab

        private void EditParamCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EditParamCombo.SelectedItem is ParameterDefinition param)
            {
                // Add/update edit column in EditDataGrid
                var key = param.UniqueKey;
                var header = $"{param.Name} {param.TypeLabel}";

                // Remove existing edit column if any
                var existing = EditDataGrid.Columns.FirstOrDefault(c => c.Header?.ToString().StartsWith(param.Name) == true);
                if (existing != null)
                {
                    EditDataGrid.Columns.Remove(existing);
                }

                // Add new column
                var column = new DataGridTextColumn
                {
                    Header = header,
                    Width = new DataGridLength(200),
                    Binding = new Binding($"Values[{key}].StringValue")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                    },
                    IsReadOnly = param.IsReadOnly
                };

                EditDataGrid.Columns.Add(column);
            }
        }

        private void EditDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Cancel) return;

            if (e.Row.Item is ElementParameterData data && e.EditingElement is System.Windows.Controls.TextBox tb)
            {
                var column = e.Column;
                var header = column.Header?.ToString() ?? "";
                
                // Extract parameter name from header
                var paramName = header.Replace(" (I)", "").Replace(" (T)", "").Trim();
                var isInstance = header.Contains("(I)");
                var key = $"{paramName}|{(isInstance ? "I" : "T")}";

                if (data.Values.TryGetValue(key, out var pv))
                {
                    var oldValue = pv.OriginalValue;
                    var newValue = tb.Text;

                    if (oldValue != newValue)
                    {
                        // Track modification
                        pv.IsModified = true;

                        // Add to pending updates
                        var update = new ParameterBatchUpdateItem
                        {
                            ElementId = data.ElementId,
                            ParameterName = paramName,
                            IsInstance = isInstance,
                            OldValue = oldValue,
                            NewValue = newValue
                        };

                        // Remove existing update for same element/param
                        _pendingUpdates.RemoveAll(u => u.ElementId == data.ElementId && u.ParameterName == paramName);
                        _pendingUpdates.Add(update);

                        // Add to undo stack
                        _undoStack.Push(update);

                        UpdateEditModifiedCount();
                    }
                }
            }
        }

        private void ApplyFormulaButton_Click(object sender, RoutedEventArgs e)
        {
            if (EditParamCombo.SelectedItem is not ParameterDefinition param)
            {
                MessageBox.Show("Please select a parameter first.", "No Parameter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var formula = FormulaTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(formula))
            {
                MessageBox.Show("Please enter a formula.", "No Formula", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (isValid, error) = FormulaParser.ValidateFormula(formula);
            if (!isValid)
            {
                MessageBox.Show($"Invalid formula: {error}", "Formula Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var elementIds = _elementDataList.Select(e => e.ElementId).ToList();

            ShowLoading("Applying formula...");
            _handler.SetRequest(ParameterRequest.ApplyFormula(elementIds, param.Name, param.IsInstance, formula));
            _externalEvent.Raise();
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0)
            {
                SetStatus("Nothing to undo");
                return;
            }

            var lastUpdate = _undoStack.Pop();

            // Restore original value in data
            var elemData = _elementDataList.FirstOrDefault(d => d.ElementId == lastUpdate.ElementId);
            if (elemData != null)
            {
                var key = $"{lastUpdate.ParameterName}|{(lastUpdate.IsInstance ? "I" : "T")}";
                if (elemData.Values.TryGetValue(key, out var pv))
                {
                    pv.StringValue = lastUpdate.OldValue;
                    pv.IsModified = pv.StringValue != pv.OriginalValue;
                }
            }

            // Remove from pending updates
            _pendingUpdates.RemoveAll(u => u.ElementId == lastUpdate.ElementId && u.ParameterName == lastUpdate.ParameterName);

            UpdateEditModifiedCount();
            EditDataGrid.Items.Refresh();
            SetStatus($"Undone: {lastUpdate.ParameterName} on element {lastUpdate.ElementId}");
        }

        private void ApplyChangesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdates.Count == 0)
            {
                MessageBox.Show("No changes to apply.", "No Changes", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Apply {_pendingUpdates.Count} changes to Revit model?", 
                "Confirm Changes", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            ShowLoading("Applying changes...");

            var batch = new ParameterUpdateBatch
            {
                Updates = _pendingUpdates.ToList(),
                Mode = UpdateMode.Direct
            };

            _handler.SetRequest(ParameterRequest.BatchUpdate(batch));
            _externalEvent.Raise();
        }

        private void UpdateEditModifiedCount()
        {
            EditModifiedCount.Text = $"{_pendingUpdates.Count} modified";
            ApplyChangesButton.IsEnabled = _pendingUpdates.Count > 0;
            UndoButton.IsEnabled = _undoStack.Count > 0;
        }

        #endregion

        #region Transfer Tab

        private void TransferMode_Changed(object sender, RoutedEventArgs e)
        {
            // Guard: Skip if window not fully loaded
            if (!IsLoaded) return;
            
            if (MapOptionsPanel != null)
                MapOptionsPanel.Visibility = TransferModeMap?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (ElementTransferPanel != null)
                ElementTransferPanel.Visibility = TransferModeElement?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MapParametersButton_Click(object sender, RoutedEventArgs e)
        {
            if (MapSourceCombo.SelectedItem is not ParameterDefinition srcParam ||
                MapTargetCombo.SelectedItem is not ParameterDefinition tgtParam)
            {
                MessageBox.Show("Please select source and target parameters.", "Selection Required", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (srcParam.UniqueKey == tgtParam.UniqueKey)
            {
                MessageBox.Show("Source and target cannot be the same parameter.", "Invalid Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var elementIds = _elementDataList.Select(d => d.ElementId).ToList();

            ShowLoading("Mapping parameters...");
            _handler.SetRequest(ParameterRequest.MapParameters(elementIds, srcParam.Name, srcParam.IsInstance, 
                tgtParam.Name, tgtParam.IsInstance));
            _externalEvent.Raise();

            AddTransferLog($"Mapped {srcParam.DisplayName} → {tgtParam.DisplayName} for {elementIds.Count} elements");
        }

        private void TransferElementButton_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(SourceElementIdTextBox.Text, out long sourceId))
            {
                MessageBox.Show("Please enter a valid source Element ID.", "Invalid ID", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targetIds = _elementDataList.Select(d => d.ElementId).Where(id => id != sourceId).ToList();
            var paramNames = _selectedParameters.Select(p => p.Name).ToList();

            if (paramNames.Count == 0)
            {
                MessageBox.Show("Please select parameters to transfer.", "No Parameters", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowLoading("Transferring values...");
            _handler.SetRequest(ParameterRequest.TransferBetweenElements(sourceId, targetIds, paramNames));
            _externalEvent.Raise();

            AddTransferLog($"Transferred {paramNames.Count} parameters from element {sourceId} to {targetIds.Count} elements");
        }

        private void ImportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                Title = "Import Parameter Values from CSV"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Parse CSV preview
                    var lines = File.ReadAllLines(dialog.FileName, Encoding.UTF8);
                    if (lines.Length < 2)
                    {
                        MessageBox.Show("CSV file is empty or has no data rows.", "Import Error", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Show import preview dialog (simplified for now)
                    var result = MessageBox.Show($"Import {lines.Length - 1} rows from:\n{dialog.FileName}\n\nFirst column should be ElementId.", 
                        "Confirm Import", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // Parse and apply
                        var headers = lines[0].Split(',').Select(h => h.Trim('"', ' ')).ToList();
                        var updates = new List<ParameterBatchUpdateItem>();

                        for (int i = 1; i < lines.Length; i++)
                        {
                            var values = ParseCsvLine(lines[i]);
                            if (values.Count < 2) continue;

                            if (!long.TryParse(values[0], out long elemId)) continue;

                            for (int j = 1; j < Math.Min(headers.Count, values.Count); j++)
                            {
                                updates.Add(new ParameterBatchUpdateItem
                                {
                                    ElementId = elemId,
                                    ParameterName = headers[j],
                                    IsInstance = true, // Assume instance for CSV import
                                    NewValue = values[j]
                                });
                            }
                        }

                        if (updates.Count > 0)
                        {
                            ShowLoading("Importing values...");
                            var batch = new ParameterUpdateBatch { Updates = updates, Mode = UpdateMode.Direct };
                            _handler.SetRequest(ParameterRequest.BatchUpdate(batch));
                            _externalEvent.Raise();

                            AddTransferLog($"Imported {updates.Count} values from CSV");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to import CSV: {ex.Message}", "Import Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                Title = "Export Parameter Values to CSV",
                FileName = $"Parameters_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                var elementIds = _elementDataList.Select(d => d.ElementId).ToList();
                var paramNames = _selectedParameters.Select(p => p.Name).ToList();

                if (paramNames.Count == 0)
                {
                    // Export all visible columns
                    paramNames = _allParameters.Select(p => p.Name).Distinct().ToList();
                }

                ShowLoading("Exporting to CSV...");
                _handler.SetRequest(ParameterRequest.ExportToCsv(elementIds, paramNames, dialog.FileName));
                _externalEvent.Raise();

                AddTransferLog($"Exported {elementIds.Count} elements to {dialog.FileName}");
            }
        }

        private void AddTransferLog(string message)
        {
            TransferLogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            if (TransferLogList.Items.Count > 100)
            {
                TransferLogList.Items.RemoveAt(TransferLogList.Items.Count - 1);
            }
        }

        private List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var inQuotes = false;
            var current = new StringBuilder();

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            result.Add(current.ToString().Trim());

            return result;
        }

        #endregion

        #region Compare Tab

        private void CompareMode_Changed(object sender, RoutedEventArgs e)
        {
            // Guard: Skip if window not fully loaded
            if (!IsLoaded) return;
            
            if (CompareTwoPanel != null)
                CompareTwoPanel.Visibility = CompareModeTwo?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (FindEmptyPanel != null)
                FindEmptyPanel.Visibility = CompareModeEmpty?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (FindDuplicatesPanel != null)
                FindDuplicatesPanel.Visibility = CompareModeDuplicate?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CompareTwoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(CompareElement1TextBox.Text, out long id1) ||
                !long.TryParse(CompareElement2TextBox.Text, out long id2))
            {
                MessageBox.Show("Please enter valid Element IDs.", "Invalid Input", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowLoading("Comparing elements...");
            _handler.SetRequest(ParameterRequest.CompareTwoElements(id1, id2));
            _externalEvent.Raise();
        }

        private void FindEmptyButton_Click(object sender, RoutedEventArgs e)
        {
            var paramNames = _selectedParameters.Select(p => p.Name).ToList();
            if (paramNames.Count == 0 && EmptyParamCombo.SelectedItem is ParameterDefinition param)
            {
                paramNames.Add(param.Name);
            }

            if (paramNames.Count == 0)
            {
                MessageBox.Show("Please select parameters to check.", "No Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var elementIds = _elementDataList.Select(d => d.ElementId).ToList();

            ShowLoading("Finding empty values...");
            _handler.SetRequest(ParameterRequest.FindEmptyValues(elementIds, paramNames));
            _externalEvent.Raise();
        }

        private void FindDuplicatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (DuplicateParamCombo.SelectedItem is not ParameterDefinition param)
            {
                MessageBox.Show("Please select a parameter.", "No Selection", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var elementIds = _elementDataList.Select(d => d.ElementId).ToList();

            ShowLoading("Finding duplicates...");
            _handler.SetRequest(ParameterRequest.FindDuplicates(elementIds, param.Name, param.IsInstance));
            _externalEvent.Raise();
        }

        private void SelectCompareElementsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = CompareDataGrid.SelectedItems.Cast<ParameterDifference>().ToList();
            if (selectedItems.Count == 0 && _currentComparison != null)
            {
                // Select both compared elements
                var ids = new List<long> { _currentComparison.ElementId1, _currentComparison.ElementId2 };
                _handler.SetRequest(ParameterRequest.SelectElements(ids));
                _externalEvent.Raise();
            }
            else if (_emptyValues.Count > 0)
            {
                var ids = _emptyValues.Select(e => e.ElementId).Distinct().ToList();
                _handler.SetRequest(ParameterRequest.SelectElements(ids));
                _externalEvent.Raise();
            }
        }

        #endregion

        #region Validate Tab

        private void AddRuleButton_Click(object sender, RoutedEventArgs e)
        {
            // Show simple input dialog
            var inputDialog = new Window
            {
                Title = "Add Validation Rule",
                Width = 350,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Parameter Name:", Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, 0);

            var textBox = new System.Windows.Controls.TextBox { Text = "Mark", Padding = new Thickness(8, 6, 8, 6) };
            Grid.SetRow(textBox, 1);

            var buttonPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            Grid.SetRow(buttonPanel, 2);

            var okButton = new Button 
            { 
                Content = "Add", 
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            okButton.Click += (s, args) => { inputDialog.DialogResult = true; inputDialog.Close(); };

            var cancelButton = new Button 
            { 
                Content = "Cancel", 
                Padding = new Thickness(20, 8, 20, 8),
                IsCancel = true
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            grid.Children.Add(label);
            grid.Children.Add(textBox);
            grid.Children.Add(buttonPanel);
            inputDialog.Content = grid;

            if (inputDialog.ShowDialog() == true)
            {
                var paramName = textBox.Text?.Trim();
                if (string.IsNullOrEmpty(paramName)) return;

                var rule = new Models.ValidationRule
                {
                    ParameterName = paramName,
                    IsInstance = true,
                    Type = ValidationType.NotEmpty,
                    Severity = ValidationSeverity.Warning,
                    Message = $"{paramName} should not be empty"
                };

                _validationRules.Add(rule);
                ValidationRulesList.ItemsSource = null;
                ValidationRulesList.ItemsSource = _validationRules;
            }
        }

        private void ValidationRulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Could show rule details or edit panel
        }

        private void RunValidationButton_Click(object sender, RoutedEventArgs e)
        {
            var enabledRules = _validationRules.Where(r => r.IsEnabled).ToList();
            if (enabledRules.Count == 0)
            {
                MessageBox.Show("No validation rules are enabled.", "No Rules", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var elementIds = _elementDataList.Select(d => d.ElementId).ToList();
            if (elementIds.Count == 0)
            {
                MessageBox.Show("No elements loaded. Please load selection first.", "No Elements", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowLoading("Validating parameters...");
            _handler.SetRequest(ParameterRequest.ValidateParameters(elementIds, enabledRules));
            _externalEvent.Raise();
        }

        private void SelectAllIssuesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentReport == null || _currentReport.Issues.Count == 0)
            {
                SetStatus("No issues to select");
                return;
            }

            var ids = _currentReport.Issues.Select(i => i.ElementId).Distinct().ToList();
            _handler.SetRequest(ParameterRequest.SelectElements(ids));
            _externalEvent.Raise();
        }

        private void ExportReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentReport == null)
            {
                SetStatus("No report to export");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                Title = "Export Validation Report",
                FileName = $"ValidationReport_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("Severity,ElementId,ElementName,Parameter,Value,Issue");

                    foreach (var issue in _currentReport.Issues)
                    {
                        sb.AppendLine($"{issue.Severity},{issue.ElementId},\"{issue.ElementName}\",\"{issue.ParameterName}\",\"{issue.CurrentValue}\",\"{issue.Message}\"");
                    }

                    File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                    SetStatus($"Report exported to {dialog.FileName}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export: {ex.Message}", "Export Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Common Actions

        private void LoadFromSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            var elementIds = GetCurrentSelectionCallback?.Invoke();
            if (elementIds == null || elementIds.Count == 0)
            {
                MessageBox.Show("No elements selected in Revit. Please select elements first.", 
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LoadElements(elementIds);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshData();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public void LoadElements(List<long> elementIds)
        {
            try
            {
                if (elementIds == null || elementIds.Count == 0) return;
                if (_handler == null || _externalEvent == null)
                {
                    SetStatus("Error: Handler not initialized");
                    return;
                }

                ShowLoading($"Loading {elementIds.Count} elements...");
                _handler.SetRequest(ParameterRequest.GetAllParameters(elementIds));
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                HideLoading();
                SetStatus($"Error: {ex.Message}");
                MessageBox.Show($"Failed to load elements:\n\n{ex.Message}", 
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshData()
        {
            var elementIds = _elementDataList.Select(d => d.ElementId).ToList();
            if (elementIds.Count > 0)
            {
                LoadElements(elementIds);
            }
        }

        private void UpdateParameterCombos()
        {
            EditParamCombo.ItemsSource = _allParameters;
            EditParamCombo.DisplayMemberPath = "DisplayName";

            MapSourceCombo.ItemsSource = _allParameters;
            MapSourceCombo.DisplayMemberPath = "DisplayName";

            MapTargetCombo.ItemsSource = _allParameters;
            MapTargetCombo.DisplayMemberPath = "DisplayName";

            EmptyParamCombo.ItemsSource = _allParameters;
            EmptyParamCombo.DisplayMemberPath = "DisplayName";

            DuplicateParamCombo.ItemsSource = _allParameters;
            DuplicateParamCombo.DisplayMemberPath = "DisplayName";
        }

        #endregion

        #region UI Helpers

        private void ShowLoading(string message = "Loading...")
        {
            LoadingText.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void HideLoading()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        #endregion
    }

    #region Value Converters

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is Visibility v && v == Visibility.Visible;
        }
    }

    #endregion
}
