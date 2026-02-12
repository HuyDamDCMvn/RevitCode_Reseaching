using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CommonFeature.Models;
using Microsoft.Win32;

namespace CommonFeature.Views
{
    #region Constants
    
    /// <summary>
    /// Column name constants for fixed columns
    /// </summary>
    public static class ColumnNames
    {
        public const string Id = "Id";
        public const string FamilyName = "FamilyName";
        public const string FamilyType = "FamilyType";
        public const string Category = "Category";
        public const string Workset = "Workset";
        public const string CreatedBy = "CreatedBy";
        public const string EditedBy = "EditedBy";
        
        public static readonly string[] FixedColumns = { Id, FamilyName, FamilyType, Category, Workset, CreatedBy, EditedBy };
    }
    
    #endregion

    #region Filter Models
    
    /// <summary>
    /// Filter item for multi-select popup
    /// </summary>
    public class FilterItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public string Value { get; set; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    
    #endregion

    /// <summary>
    /// Code-behind for InfoWindow.xaml
    /// Optimized with: Performance caching, Undo support, Robustness improvements
    /// </summary>
    public partial class InfoWindow : Window
    {
        #region Fields
        
        private List<ElementInfo> _allElementInfos = new();
        private ICollectionView _collectionView;
        private Action _refreshCallback;
        
        // Multi-select filter sets - stores currently selected values for each column
        private Dictionary<string, HashSet<string>> _selectedFilters = new();
        
        // PERFORMANCE: Cache for filter distinct values
        private Dictionary<string, List<string>> _filterValuesCache = new();
        private bool _filterCacheValid = false;
        
        // Current filter column for popup
        private string _currentFilterColumn;
        private List<FilterItem> _currentFilterItems = new();
        
        // Parameter selection
        private List<ParameterInfo> _availableParameters = new();
        private List<string> _addedParamColumns = new();
        
        // Track parameter metadata (isInstance)
        private Dictionary<string, bool> _parameterIsInstance = new();
        
        // ROBUSTNESS: Track element being edited to prevent filter issues
        private ElementInfo _editingElement = null;
        private string _editingParamName = null;
        
        // UX: Undo stack for cell edits
        private Stack<UndoItem> _undoStack = new();
        private const int MaxUndoItems = 50;
        
        // Light green color for modified cells
        private static readonly SolidColorBrush ModifiedCellBrush = new(Color.FromRgb(200, 230, 201));
        
        #endregion

        #region Callbacks
        
        // Callback to request parameters from Revit (synchronous, used during data loading)
        public Func<List<long>, List<ParameterInfo>> GetParametersCallback { get; set; }
        public Func<List<long>, List<string>, Dictionary<long, ParameterValuesResult>> GetParameterValuesCallback { get; set; }
        
        // ExternalEvent for update operations (asynchronous, Revit-safe)
        public Action<List<ParameterUpdateItem>> RaiseUpdateEvent { get; set; }
        
        // ExternalEvent for select elements
        public Action<List<long>> RaiseSelectElementsEvent { get; set; }
        
        // ExternalEvent for create section box
        public Action<List<long>> RaiseCreateSectionBoxEvent { get; set; }
        
        #endregion

        #region Constructor & Lifecycle
        
        public InfoWindow()
        {
            InitializeComponent();
            
            // ROBUSTNESS: Wire up closing event for cleanup
            Closing += InfoWindow_Closing;
            
            // PERFORMANCE: Wire up LoadingRow for highlight restoration
            InfoDataGrid.LoadingRow += InfoDataGrid_LoadingRow;
        }

        private void InfoWindow_Closing(object sender, CancelEventArgs e)
        {
            // ROBUSTNESS: Cleanup resources
            try
            {
                _undoStack.Clear();
                _filterValuesCache.Clear();
                _editingElement = null;
                
                // Unsubscribe events
                InfoDataGrid.LoadingRow -= InfoDataGrid_LoadingRow;
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        #endregion

        #region Public Methods
        
        /// <summary>
        /// Called by handler when update is completed (from Revit thread)
        /// </summary>
        public void OnUpdateCompleted()
        {
            // Clear modification tracking after update
            foreach (var info in _allElementInfos)
            {
                info.ClearModifications();
            }
            
            // Clear undo stack since changes are committed
            _undoStack.Clear();
            
            // Reset cell backgrounds and update UI
            InvalidateFilterCache();
            UpdateCellBackgrounds();
            UpdateModifiedCount();
            UpdateValueButton.IsEnabled = false;
        }

        /// <summary>
        /// Set the data source for the DataGrid.
        /// </summary>
        public void SetData(List<ElementInfo> elementInfos, Action refreshCallback = null)
        {
            _allElementInfos = elementInfos ?? new List<ElementInfo>();
            _refreshCallback = refreshCallback;
            
            // Initialize filter sets with all values selected
            InitializeFilterSets();
            InvalidateFilterCache();
            
            // Setup collection view for filtering
            _collectionView = CollectionViewSource.GetDefaultView(_allElementInfos);
            _collectionView.Filter = FilterPredicate;
            
            InfoDataGrid.ItemsSource = _collectionView;
            UpdateCounts();
        }
        
        #endregion

        #region Filter Logic
        
        /// <summary>
        /// Initialize filter sets with ALL values selected (for clear filter)
        /// </summary>
        private void InitializeFilterSets()
        {
            _selectedFilters.Clear();
            
            // Fixed columns - select ALL values
            _selectedFilters[ColumnNames.Id] = new HashSet<string>(_allElementInfos.Select(e => e.Id.ToString()).Distinct());
            _selectedFilters[ColumnNames.FamilyName] = new HashSet<string>(_allElementInfos.Select(e => e.FamilyName).Distinct());
            _selectedFilters[ColumnNames.FamilyType] = new HashSet<string>(_allElementInfos.Select(e => e.FamilyType).Distinct());
            _selectedFilters[ColumnNames.Category] = new HashSet<string>(_allElementInfos.Select(e => e.Category).Distinct());
            _selectedFilters[ColumnNames.Workset] = new HashSet<string>(_allElementInfos.Select(e => e.Workset).Distinct());
            _selectedFilters[ColumnNames.CreatedBy] = new HashSet<string>(_allElementInfos.Select(e => e.CreatedBy).Distinct());
            _selectedFilters[ColumnNames.EditedBy] = new HashSet<string>(_allElementInfos.Select(e => e.EditedBy).Distinct());
            
            // Parameter columns - select ALL values
            foreach (var paramName in _addedParamColumns)
            {
                _selectedFilters[paramName] = new HashSet<string>(
                    _allElementInfos.Select(e => e.GetParameterValue(paramName)).Distinct());
            }
        }

        private bool FilterPredicate(object obj)
        {
            if (obj is not ElementInfo info) return false;
            
            // ROBUSTNESS: Always show the element being edited
            if (_editingElement != null && info.Id == _editingElement.Id)
            {
                return true;
            }

            // Check fixed column filters
            if (!CheckFilter(ColumnNames.Id, info.Id.ToString())) return false;
            if (!CheckFilter(ColumnNames.FamilyName, info.FamilyName)) return false;
            if (!CheckFilter(ColumnNames.FamilyType, info.FamilyType)) return false;
            if (!CheckFilter(ColumnNames.Category, info.Category)) return false;
            if (!CheckFilter(ColumnNames.Workset, info.Workset)) return false;
            if (!CheckFilter(ColumnNames.CreatedBy, info.CreatedBy)) return false;
            if (!CheckFilter(ColumnNames.EditedBy, info.EditedBy)) return false;
            
            // Check parameter column filters
            foreach (var paramName in _addedParamColumns)
            {
                var value = info.GetParameterValue(paramName);
                if (!CheckFilter(paramName, value)) return false;
            }

            return true;
        }
        
        private bool CheckFilter(string columnName, string value)
        {
            if (_selectedFilters.TryGetValue(columnName, out var filter) && filter.Count > 0)
            {
                return filter.Contains(value);
            }
            return true;
        }

        /// <summary>
        /// Get currently visible (filtered) elements
        /// </summary>
        private List<ElementInfo> GetFilteredElements()
        {
            return _collectionView?.Cast<ElementInfo>().ToList() ?? new List<ElementInfo>();
        }
        
        /// <summary>
        /// PERFORMANCE: Invalidate filter cache when data changes
        /// </summary>
        private void InvalidateFilterCache()
        {
            _filterCacheValid = false;
            _filterValuesCache.Clear();
        }
        
        /// <summary>
        /// PERFORMANCE: Get cached distinct values for a column
        /// </summary>
        private List<string> GetCachedDistinctValues(string columnName)
        {
            if (_filterCacheValid && _filterValuesCache.TryGetValue(columnName, out var cached))
            {
                return cached;
            }
            
            var visibleElements = GetFilteredElements();
            var values = columnName switch
            {
                ColumnNames.Id => visibleElements.Select(x => x.Id.ToString()).Distinct(),
                ColumnNames.FamilyName => visibleElements.Select(x => x.FamilyName).Distinct(),
                ColumnNames.FamilyType => visibleElements.Select(x => x.FamilyType).Distinct(),
                ColumnNames.Category => visibleElements.Select(x => x.Category).Distinct(),
                ColumnNames.Workset => visibleElements.Select(x => x.Workset).Distinct(),
                ColumnNames.CreatedBy => visibleElements.Select(x => x.CreatedBy).Distinct(),
                ColumnNames.EditedBy => visibleElements.Select(x => x.EditedBy).Distinct(),
                _ => visibleElements.Select(x => x.GetParameterValue(columnName)).Distinct()
            };
            
            var list = values.OrderBy(v => v).ToList();
            _filterValuesCache[columnName] = list;
            return list;
        }
        
        #endregion

        #region UI Updates
        
        private void UpdateCounts()
        {
            int totalCount = _allElementInfos?.Count ?? 0;
            int filteredCount = _collectionView?.Cast<object>().Count() ?? 0;

            CountText.Text = $"{totalCount} element(s) total";
            
            if (filteredCount < totalCount)
            {
                StatusText.Text = $"Showing {filteredCount} of {totalCount} (filtered)";
            }
            else
            {
                StatusText.Text = $"Showing all {totalCount} element(s)";
            }
        }
        
        private void UpdateModifiedCount()
        {
            int modifiedCount = _allElementInfos?.Sum(e => e.ModifiedParameters.Count) ?? 0;
            UpdateValueButton.IsEnabled = modifiedCount > 0;
            
            if (modifiedCount > 0)
            {
                StatusText.Text = $"{modifiedCount} value(s) modified - Click 'Update Value' to apply (Ctrl+Z to undo)";
            }
        }
        
        /// <summary>
        /// PERFORMANCE: Handle row loading for virtualization - restore highlights
        /// </summary>
        private void InfoDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item is not ElementInfo elementInfo) return;
            
            // Restore highlights for modified cells when row is recycled
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                RestoreRowHighlights(e.Row, elementInfo);
            }));
        }
        
        private void RestoreRowHighlights(DataGridRow row, ElementInfo elementInfo)
        {
            if (row == null || elementInfo == null) return;
            
            foreach (var col in InfoDataGrid.Columns)
            {
                var paramName = GetParamNameFromColumnHeader(col);
                if (string.IsNullOrEmpty(paramName)) continue;
                
                var cell = GetCell(row, col);
                if (cell == null) continue;
                
                if (elementInfo.IsParameterModified(paramName))
                {
                    cell.Background = ModifiedCellBrush;
                }
                else
                {
                    cell.ClearValue(DataGridCell.BackgroundProperty);
                }
            }
        }
        
        private void UpdateCellBackgrounds()
        {
            foreach (var item in InfoDataGrid.Items)
            {
                if (item is not ElementInfo elementInfo) continue;
                
                var row = InfoDataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                if (row == null) continue;
                
                RestoreRowHighlights(row, elementInfo);
            }
        }
        
        #endregion

        #region Cell Editing
        
        private void InfoDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            try
            {
                var column = e.Column;
                if (column == null) return;
                
                var headerText = GetColumnHeaderText(column);
                
                // Only allow editing for parameter columns (contains "(I)" or "(T)")
                if (!headerText.Contains("(I)") && !headerText.Contains("(T)"))
                {
                    e.Cancel = true;
                    return;
                }
                
                // Extract parameter name
                var paramName = ExtractParamName(headerText);
                
                // Check if this element's parameter is read-only
                if (e.Row.Item is ElementInfo elementInfo)
                {
                    if (elementInfo.IsParameterReadOnly(paramName))
                    {
                        e.Cancel = true;
                        
                        // Show popup notification for read-only parameter
                        MessageBox.Show(
                            $"Cannot edit parameter '{paramName}'.\n\n" +
                            "This parameter is read-only (BuiltInParameter or system parameter).\n" +
                            "Read-only parameters cannot be modified through this interface.",
                            "Read-Only Parameter",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        
                        StatusText.Text = $"'{paramName}' is read-only";
                        return;
                    }
                    
                    // ROBUSTNESS: Track element being edited to bypass filter
                    _editingElement = elementInfo;
                    _editingParamName = paramName;
                }
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                StatusText.Text = $"Edit error: {ex.Message}";
            }
        }

        private void InfoDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            try
            {
                if (e.EditAction == DataGridEditAction.Cancel)
                {
                    // Clear editing state on cancel
                    _editingElement = null;
                    _editingParamName = null;
                    return;
                }
                
                if (e.Row.Item is not ElementInfo elementInfo)
                {
                    _editingElement = null;
                    _editingParamName = null;
                    return;
                }
                if (e.EditingElement is not TextBox textBox)
                {
                    _editingElement = null;
                    _editingParamName = null;
                    return;
                }
                
                var column = e.Column;
                var headerText = GetColumnHeaderText(column);
                var paramName = ExtractParamName(headerText);
                
                var newValue = textBox.Text;
                var oldValue = elementInfo.GetParameterValue(paramName);
                
                // Store row reference for highlight
                var row = e.Row;
                
                if (newValue != oldValue)
                {
                    // VALIDATION: Check if value matches expected data type
                    var validationError = elementInfo.ValidateValue(paramName, newValue);
                    if (validationError != null)
                    {
                        // Show validation error and cancel the edit
                        e.Cancel = true;
                        
                        // Get the expected data type for user-friendly message
                        var dataType = elementInfo.GetParameterDataType(paramName);
                        var dataTypeDisplay = GetDataTypeDisplayName(dataType);
                        
                        MessageBox.Show(
                            $"Invalid value for parameter '{paramName}'.\n\n" +
                            $"Expected: {dataTypeDisplay}\n" +
                            $"Entered: '{newValue}'\n\n" +
                            validationError,
                            "Invalid Input",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        
                        // Restore original value in textbox
                        textBox.Text = oldValue;
                        StatusText.Text = $"Invalid input for '{paramName}' - please enter a {dataTypeDisplay.ToLower()} value";
                        return;
                    }
                    
                    // UX: Add to undo stack
                    PushUndo(new UndoItem(elementInfo, paramName, oldValue, newValue));
                    
                    // Add new value to filter set so row stays visible BEFORE clearing _editingElement
                    if (_selectedFilters.TryGetValue(paramName, out var filterSet) && filterSet.Count > 0)
                    {
                        filterSet.Add(newValue);
                    }
                    
                    // Update the model (does NOT raise PropertyChanged)
                    elementInfo.SetParameterValue(paramName, newValue);
                    
                    // Invalidate filter cache
                    InvalidateFilterCache();
                    
                    // Update UI after edit mode ends - KEEP _editingElement until UI update completes
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                    {
                        // Now safe to clear editing state
                        _editingElement = null;
                        _editingParamName = null;
                        
                        var cell = GetCell(row, column);
                        if (cell != null)
                        {
                            cell.Background = ModifiedCellBrush;
                        }
                        
                        UpdateModifiedCount();
                        StatusText.Text = $"Modified: '{paramName}' = '{newValue}' (Ctrl+Z to undo)";
                    }));
                }
                else
                {
                    // No change - clear editing state immediately
                    _editingElement = null;
                    _editingParamName = null;
                }
            }
            catch (Exception ex)
            {
                // Always clear editing state on error
                _editingElement = null;
                _editingParamName = null;
                StatusText.Text = $"Edit error: {ex.Message}";
            }
        }
        
        /// <summary>
        /// Get user-friendly display name for data type
        /// </summary>
        private string GetDataTypeDisplayName(string dataType)
        {
            return dataType switch
            {
                ParameterDataType.Integer => "Integer (whole number)",
                ParameterDataType.Double => "Number (decimal allowed)",
                ParameterDataType.YesNo => "Yes/No",
                ParameterDataType.ElementId => "Element ID",
                ParameterDataType.String => "Text",
                _ => "Text"
            };
        }
        
        private string ExtractParamName(string headerText)
        {
            if (headerText.EndsWith(" (I)"))
                return headerText[..^4];
            if (headerText.EndsWith(" (T)"))
                return headerText[..^4];
            return headerText;
        }

        private string GetColumnHeaderText(DataGridColumn column)
        {
            if (column.Header is string headerStr)
                return headerStr;
            
            // For Grid headers (programmatically created parameter columns)
            if (column.Header is Grid headerGrid)
            {
                foreach (var child in headerGrid.Children)
                {
                    if (child is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
                    {
                        return textBlock.Text;
                    }
                }
            }
            
            // For templated headers, try to get from binding path
            if (column is DataGridTextColumn textColumn)
            {
                var binding = textColumn.Binding as Binding;
                if (binding != null)
                {
                    var path = binding.Path?.Path ?? "";
                    return path switch
                    {
                        "Id" => "ID",
                        "FamilyName" => "Family Name",
                        "FamilyType" => "Family Type",
                        "Category" => "Category",
                        "Workset" => "Workset",
                        "CreatedBy" => "Created By",
                        "EditedBy" => "Edited By",
                        _ when path.StartsWith("Parameters[") => ExtractParamNameFromPath(path),
                        _ => path
                    };
                }
            }
            
            return "";
        }
        
        private string GetParamNameFromColumnHeader(DataGridColumn column)
        {
            var headerText = GetColumnHeaderText(column);
            if (headerText.Contains("(I)") || headerText.Contains("(T)"))
            {
                return ExtractParamName(headerText);
            }
            return null;
        }

        private string ExtractParamNameFromPath(string path)
        {
            if (path.StartsWith("Parameters[") && path.EndsWith("]"))
            {
                var paramName = path.Substring(11, path.Length - 12);
                if (_parameterIsInstance.TryGetValue(paramName, out var isInstance))
                {
                    return $"{paramName} {(isInstance ? "(I)" : "(T)")}";
                }
                return paramName;
            }
            return path;
        }
        
        #endregion

        #region Undo Support
        
        private class UndoItem
        {
            public ElementInfo Element { get; }
            public string ParamName { get; }
            public string OldValue { get; }
            public string NewValue { get; }
            
            public UndoItem(ElementInfo element, string paramName, string oldValue, string newValue)
            {
                Element = element;
                ParamName = paramName;
                OldValue = oldValue;
                NewValue = newValue;
            }
        }
        
        private void PushUndo(UndoItem item)
        {
            _undoStack.Push(item);
            
            // Limit undo stack size
            if (_undoStack.Count > MaxUndoItems)
            {
                var tempStack = new Stack<UndoItem>(_undoStack.Take(MaxUndoItems).Reverse());
                _undoStack = tempStack;
            }
        }
        
        /// <summary>
        /// UX: Undo last edit (Ctrl+Z)
        /// </summary>
        public bool Undo()
        {
            if (_undoStack.Count == 0) return false;
            
            var undoItem = _undoStack.Pop();
            
            // Temporarily track element to prevent filter from hiding it
            _editingElement = undoItem.Element;
            
            try
            {
                // Update filter set BEFORE changing value
                if (_selectedFilters.TryGetValue(undoItem.ParamName, out var filterSet))
                {
                    filterSet.Add(undoItem.OldValue);
                }
                
                // Restore old value in Parameters dictionary directly
                undoItem.Element.Parameters[undoItem.ParamName] = undoItem.OldValue;
                
                // Check if parameter is still modified (comparing to original)
                if (undoItem.Element.OriginalParameters.TryGetValue(undoItem.ParamName, out var original))
                {
                    if (original == undoItem.OldValue)
                    {
                        undoItem.Element.ModifiedParameters.Remove(undoItem.ParamName);
                    }
                    else
                    {
                        undoItem.Element.ModifiedParameters.Add(undoItem.ParamName);
                    }
                }
                
                InvalidateFilterCache();
                _collectionView?.Refresh();
                UpdateCellBackgrounds();
                UpdateModifiedCount();
                
                StatusText.Text = $"Undone: '{undoItem.ParamName}' restored to '{undoItem.OldValue}'";
                return true;
            }
            finally
            {
                _editingElement = null;
            }
        }
        
        // Handle Ctrl+Z in window
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (Undo())
                {
                    e.Handled = true;
                }
            }
        }
        
        #endregion

        #region Cell Helpers
        
        private DataGridCell GetCell(DataGridRow row, DataGridColumn column)
        {
            if (row == null || column == null) return null;
            
            var presenter = FindVisualChild<DataGridCellsPresenter>(row);
            if (presenter == null) return null;
            
            var index = InfoDataGrid.Columns.IndexOf(column);
            var cell = presenter.ItemContainerGenerator.ContainerFromIndex(index) as DataGridCell;
            
            if (cell == null)
            {
                InfoDataGrid.ScrollIntoView(row, column);
                cell = presenter.ItemContainerGenerator.ContainerFromIndex(index) as DataGridCell;
            }
            
            return cell;
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var grandChild = FindVisualChild<T>(child);
                if (grandChild != null) return grandChild;
            }
            return null;
        }
        
        #endregion

        #region Update Values

        private void UpdateValueButton_Click(object sender, RoutedEventArgs e)
        {
            var updates = new List<ParameterUpdateItem>();
            
            foreach (var info in _allElementInfos)
            {
                var modifiedParams = info.ModifiedParameters.ToList();
                foreach (var paramName in modifiedParams)
                {
                    var newValue = info.GetParameterValue(paramName);
                    var isInstance = _parameterIsInstance.TryGetValue(paramName, out var isInst) && isInst;
                    
                    updates.Add(new ParameterUpdateItem(info.Id, paramName, newValue, isInstance));
                }
            }
            
            if (updates.Count == 0)
            {
                MessageBox.Show("No modified values to update.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            var result = MessageBox.Show(
                $"Update {updates.Count} parameter value(s) in Revit model?\n\n" +
                "This will modify the model. Save your work first.",
                "Confirm Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result != MessageBoxResult.Yes) return;
            
            if (RaiseUpdateEvent != null)
            {
                UpdateValueButton.IsEnabled = false;
                StatusText.Text = "Updating values... (waiting for Revit)";
                RaiseUpdateEvent(updates);
            }
            else
            {
                MessageBox.Show("Update event not available. Please reopen the window.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Add Parameter

        private void AddParamButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = InfoDataGrid.SelectedCells
                .Select(c => c.Item)
                .OfType<ElementInfo>()
                .Distinct()
                .ToList();
            
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("Please select at least one row to get available parameters.", 
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var elementIds = selectedItems.Select(x => x.Id).ToList();

            if (GetParametersCallback != null)
            {
                _availableParameters = GetParametersCallback(elementIds);
                
                _availableParameters = _availableParameters
                    .Where(p => !_addedParamColumns.Contains(p.Name))
                    .ToList();
                
                if (_availableParameters.Count == 0)
                {
                    MessageBox.Show("No additional parameters available.", 
                        "No Parameters", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ParamItemsControl.ItemsSource = _availableParameters;
                ParamSearchBox.Text = "";
                
                ParamPopup.PlacementTarget = AddParamButton;
                ParamPopup.IsOpen = true;
            }
            else
            {
                MessageBox.Show("Parameter retrieval not available.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ParamSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = ParamSearchBox.Text?.Trim() ?? "";
            
            if (string.IsNullOrEmpty(searchText))
            {
                ParamItemsControl.ItemsSource = _availableParameters;
            }
            else
            {
                ParamItemsControl.ItemsSource = _availableParameters
                    .Where(p => p.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void ParamSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var param in _availableParameters)
                param.IsSelected = true;
            ParamItemsControl.Items.Refresh();
        }

        private void ParamClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var param in _availableParameters)
                param.IsSelected = false;
            ParamItemsControl.Items.Refresh();
        }

        private void AddParamColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedParams = _availableParameters.Where(p => p.IsSelected).ToList();
            
            if (selectedParams.Count == 0)
            {
                MessageBox.Show("Please select at least one parameter.", 
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var elementIds = _allElementInfos.Select(x => x.Id).ToList();
            var paramNames = selectedParams.Select(p => p.Name).ToList();

            if (GetParameterValuesCallback != null)
            {
                var paramValues = GetParameterValuesCallback(elementIds, paramNames);

                foreach (var info in _allElementInfos)
                {
                    if (paramValues.TryGetValue(info.Id, out var result))
                    {
                        foreach (var kvp in result.Values)
                        {
                            info.Parameters[kvp.Key] = kvp.Value;
                            info.OriginalParameters[kvp.Key] = kvp.Value;
                        }
                        
                        foreach (var readOnlyParam in result.ReadOnlyParams)
                        {
                            info.ReadOnlyParameters.Add(readOnlyParam);
                        }
                        
                        // Store data types for validation
                        foreach (var kvp in result.DataTypes)
                        {
                            info.ParameterDataTypes[kvp.Key] = kvp.Value;
                        }
                    }
                }

                foreach (var param in selectedParams)
                {
                    AddParameterColumn(param.Name, param.IsInstance);
                    _addedParamColumns.Add(param.Name);
                    _parameterIsInstance[param.Name] = param.IsInstance;
                    
                    _selectedFilters[param.Name] = new HashSet<string>(
                        _allElementInfos.Select(e => e.GetParameterValue(param.Name)).Distinct());
                }

                InvalidateFilterCache();
                InfoDataGrid.Items.Refresh();
                
                StatusText.Text = $"Added {selectedParams.Count} parameter column(s)";
            }

            ParamPopup.IsOpen = false;
        }

        private void AddParameterColumn(string paramName, bool isInstance)
        {
            var headerText = $"{paramName} {(isInstance ? "(I)" : "(T)")}";
            
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            var textBlock = new TextBlock
            {
                Text = headerText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(textBlock, 0);
            
            var filterButton = new Button
            {
                Tag = paramName,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "Filter",
                Cursor = Cursors.Hand,
                Content = new TextBlock
                {
                    Text = "▼",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117))
                }
            };
            filterButton.Click += ParamFilterButton_Click;
            Grid.SetColumn(filterButton, 1);
            
            headerGrid.Children.Add(textBlock);
            headerGrid.Children.Add(filterButton);
            
            // Create editable column - Use TwoWay with Explicit trigger
            var column = new DataGridTextColumn
            {
                Header = headerGrid,
                Binding = new Binding($"Parameters[{paramName}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                    TargetNullValue = "-",
                    FallbackValue = "-"
                },
                Width = new DataGridLength(140),
                IsReadOnly = false
            };

            InfoDataGrid.Columns.Add(column);
        }

        private void ParamFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string paramName)
            {
                ShowFilterForParameter(paramName);
            }
        }

        private void RemoveParameterColumn(string paramName)
        {
            DataGridColumn columnToRemove = null;
            foreach (var col in InfoDataGrid.Columns)
            {
                var colParamName = GetParamNameFromColumn(col);
                if (colParamName == paramName)
                {
                    columnToRemove = col;
                    break;
                }
            }
            
            if (columnToRemove != null)
            {
                InfoDataGrid.Columns.Remove(columnToRemove);
                _addedParamColumns.Remove(paramName);
                _parameterIsInstance.Remove(paramName);
                _selectedFilters.Remove(paramName);
                
                foreach (var info in _allElementInfos)
                {
                    info.Parameters.Remove(paramName);
                    info.OriginalParameters.Remove(paramName);
                    info.ModifiedParameters.Remove(paramName);
                    info.ReadOnlyParameters.Remove(paramName); // Also clear read-only status
                }
                
                // Clear related undo items
                _undoStack = new Stack<UndoItem>(
                    _undoStack.Where(u => u.ParamName != paramName).Reverse());
                
                InvalidateFilterCache();
                _collectionView?.Refresh();
                UpdateCounts();
                UpdateModifiedCount();
                StatusText.Text = $"Removed column '{paramName}'";
            }
        }

        private string GetParamNameFromColumn(DataGridColumn col)
        {
            if (col.Header is Grid headerGrid)
            {
                foreach (var child in headerGrid.Children)
                {
                    if (child is Button btn && btn.Tag is string paramName)
                    {
                        return paramName;
                    }
                }
            }
            else if (col.Header is string headerText)
            {
                if (headerText.EndsWith(" (I)"))
                    return headerText[..^4];
                if (headerText.EndsWith(" (T)"))
                    return headerText[..^4];
            }
            return null;
        }

        private void InfoDataGrid_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dep = (DependencyObject)e.OriginalSource;
            
            // Check for column header first
            var depForHeader = dep;
            while (depForHeader != null && !(depForHeader is DataGridColumnHeader))
            {
                depForHeader = VisualTreeHelper.GetParent(depForHeader);
            }
            
            if (depForHeader is DataGridColumnHeader header && header.Column != null)
            {
                var paramName = GetParamNameFromColumn(header.Column);
                
                if (!string.IsNullOrEmpty(paramName) && _addedParamColumns.Contains(paramName))
                {
                    var contextMenu = new ContextMenu();
                    
                    var filterItem = new MenuItem { Header = "Filter..." };
                    filterItem.Click += (s, args) => ShowFilterForParameter(paramName);
                    contextMenu.Items.Add(filterItem);
                    
                    contextMenu.Items.Add(new Separator());
                    
                    var removeItem = new MenuItem { Header = "Remove Column" };
                    removeItem.Click += (s, args) => RemoveParameterColumn(paramName);
                    contextMenu.Items.Add(removeItem);
                    
                    contextMenu.IsOpen = true;
                    e.Handled = true;
                    return;
                }
            }
            
            // Check for cell (DataGridCell)
            var depForCell = dep;
            while (depForCell != null && !(depForCell is DataGridCell))
            {
                depForCell = VisualTreeHelper.GetParent(depForCell);
            }
            
            if (depForCell is DataGridCell cell)
            {
                // Get the DataGridRow containing this cell
                var row = DataGridRow.GetRowContainingElement(cell);
                if (row?.Item is ElementInfo elementInfo)
                {
                    // Get selected elements (if multiple rows selected, use them; otherwise use clicked row)
                    var selectedElements = GetSelectedElementInfos();
                    if (selectedElements.Count == 0 || !selectedElements.Any(ei => ei.Id == elementInfo.Id))
                    {
                        selectedElements = new List<ElementInfo> { elementInfo };
                    }
                    
                    // Get cell value for copy
                    var column = cell.Column;
                    string cellValue = GetCellValueForCopy(elementInfo, column);
                    
                    // Check if this is a modified parameter cell
                    var paramName = GetParamNameFromColumn(column);
                    bool isModifiedParamCell = !string.IsNullOrEmpty(paramName) && 
                                                _addedParamColumns.Contains(paramName) && 
                                                elementInfo.IsParameterModified(paramName);
                    
                    ShowCellContextMenu(selectedElements, cellValue, paramName, isModifiedParamCell, elementInfo);
                    e.Handled = true;
                }
            }
        }
        
        /// <summary>
        /// Get selected ElementInfo objects from DataGrid
        /// </summary>
        private List<ElementInfo> GetSelectedElementInfos()
        {
            var selected = new List<ElementInfo>();
            foreach (var item in InfoDataGrid.SelectedItems)
            {
                if (item is ElementInfo ei)
                {
                    selected.Add(ei);
                }
            }
            return selected;
        }
        
        /// <summary>
        /// Get cell value for copy operation
        /// </summary>
        private string GetCellValueForCopy(ElementInfo elementInfo, DataGridColumn column)
        {
            if (column == null) return "";
            
            var headerText = GetColumnHeaderText(column);
            
            // Fixed columns
            if (headerText == ColumnNames.Id) return elementInfo.Id.ToString();
            if (headerText == ColumnNames.FamilyName) return elementInfo.FamilyName;
            if (headerText == ColumnNames.FamilyType) return elementInfo.FamilyType;
            if (headerText == ColumnNames.Category) return elementInfo.Category;
            if (headerText == ColumnNames.Workset) return elementInfo.Workset;
            if (headerText == ColumnNames.CreatedBy) return elementInfo.CreatedBy;
            if (headerText == ColumnNames.EditedBy) return elementInfo.EditedBy;
            
            // Parameter columns
            var paramName = ExtractParamName(headerText);
            if (!string.IsNullOrEmpty(paramName))
            {
                return elementInfo.GetParameterValue(paramName);
            }
            
            return "";
        }
        
        /// <summary>
        /// Show context menu for cell right-click
        /// </summary>
        private void ShowCellContextMenu(List<ElementInfo> selectedElements, string cellValue, 
            string paramName, bool isModifiedParamCell, ElementInfo clickedElement)
        {
            var contextMenu = new ContextMenu();
            
            // Disabled style brush
            var disabledBrush = new SolidColorBrush(Colors.Gray);
            
            // Check availability conditions
            bool hasCellValue = !string.IsNullOrEmpty(cellValue) && cellValue != "-";
            bool hasElements = selectedElements != null && selectedElements.Count > 0;
            bool canRevert = isModifiedParamCell && !string.IsNullOrEmpty(paramName);
            bool hasSelectCallback = RaiseSelectElementsEvent != null;
            bool hasSectionBoxCallback = RaiseCreateSectionBoxEvent != null;
            
            // 1. Copy Value
            var copyItem = new MenuItem 
            { 
                Header = "Copy Value",
                IsEnabled = hasCellValue
            };
            if (!hasCellValue)
            {
                copyItem.Foreground = disabledBrush;
                copyItem.ToolTip = "No value to copy (cell is empty or '-')";
            }
            copyItem.Click += (s, args) =>
            {
                try
                {
                    if (hasCellValue)
                    {
                        Clipboard.SetText(cellValue);
                        StatusText.Text = $"Copied: '{cellValue}'";
                    }
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Copy failed: {ex.Message}";
                }
            };
            contextMenu.Items.Add(copyItem);
            
            // 2. Revert to Original Value (always show, but disable if not applicable)
            var revertItem = new MenuItem 
            { 
                Header = "Revert to Original Value",
                IsEnabled = canRevert
            };
            if (!canRevert)
            {
                revertItem.Foreground = disabledBrush;
                if (string.IsNullOrEmpty(paramName) || !_addedParamColumns.Contains(paramName ?? ""))
                {
                    revertItem.ToolTip = "Only available for parameter columns";
                }
                else
                {
                    revertItem.ToolTip = "Cell has not been modified";
                }
            }
            else
            {
                revertItem.Click += (s, args) => RevertToOriginalValue(clickedElement, paramName);
            }
            contextMenu.Items.Add(revertItem);
            
            contextMenu.Items.Add(new Separator());
            
            // 3. Select Element
            bool canSelect = hasElements && hasSelectCallback;
            var selectItem = new MenuItem 
            { 
                Header = selectedElements.Count > 1 
                    ? $"Select Elements ({selectedElements.Count})" 
                    : "Select Element",
                IsEnabled = canSelect
            };
            if (!canSelect)
            {
                selectItem.Foreground = disabledBrush;
                selectItem.ToolTip = !hasElements 
                    ? "No elements selected" 
                    : "Select elements feature not available";
            }
            else
            {
                selectItem.Click += (s, args) =>
                {
                    var elementIds = selectedElements.Select(e => e.Id).ToList();
                    RaiseSelectElementsEvent(elementIds);
                    StatusText.Text = $"Selecting {elementIds.Count} element(s)...";
                };
            }
            contextMenu.Items.Add(selectItem);
            
            // 4. Create Section Box
            bool canCreateSectionBox = hasElements && hasSectionBoxCallback;
            var sectionBoxItem = new MenuItem 
            { 
                Header = selectedElements.Count > 1 
                    ? $"Create Section Box ({selectedElements.Count} elements)" 
                    : "Create Section Box",
                IsEnabled = canCreateSectionBox
            };
            if (!canCreateSectionBox)
            {
                sectionBoxItem.Foreground = disabledBrush;
                sectionBoxItem.ToolTip = !hasElements 
                    ? "No elements selected" 
                    : "Create section box feature not available";
            }
            else
            {
                sectionBoxItem.Click += (s, args) =>
                {
                    var elementIds = selectedElements.Select(e => e.Id).ToList();
                    RaiseCreateSectionBoxEvent(elementIds);
                    StatusText.Text = $"Creating section box for {elementIds.Count} element(s)...";
                };
            }
            contextMenu.Items.Add(sectionBoxItem);
            
            contextMenu.IsOpen = true;
        }
        
        /// <summary>
        /// Revert a modified parameter cell to its original value
        /// </summary>
        private void RevertToOriginalValue(ElementInfo elementInfo, string paramName)
        {
            if (elementInfo == null || string.IsNullOrEmpty(paramName)) return;
            
            // Check if we have original value
            if (!elementInfo.OriginalParameters.TryGetValue(paramName, out var originalValue))
            {
                StatusText.Text = $"No original value stored for '{paramName}'";
                return;
            }
            
            // Temporarily track element to prevent filter issues
            _editingElement = elementInfo;
            
            try
            {
                // Update filter set
                if (_selectedFilters.TryGetValue(paramName, out var filterSet))
                {
                    filterSet.Add(originalValue);
                }
                
                // Restore original value
                elementInfo.Parameters[paramName] = originalValue;
                elementInfo.ModifiedParameters.Remove(paramName);
                
                // Invalidate cache and refresh
                InvalidateFilterCache();
                _collectionView?.Refresh();
                
                // Update cell background
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    _editingElement = null;
                    UpdateCellBackgrounds();
                    UpdateModifiedCount();
                    StatusText.Text = $"Reverted '{paramName}' to original value: '{originalValue}'";
                }));
            }
            catch (Exception ex)
            {
                _editingElement = null;
                StatusText.Text = $"Revert failed: {ex.Message}";
            }
        }

        private void ShowFilterForParameter(string paramName)
        {
            _currentFilterColumn = paramName;
            
            // PERFORMANCE: Use cached values
            var allValues = GetCachedDistinctValues(paramName);
            var selectedValues = _selectedFilters.TryGetValue(paramName, out var set) ? set : new HashSet<string>();

            _currentFilterItems = allValues
                .Select(v => new FilterItem 
                { 
                    Value = v, 
                    IsSelected = selectedValues.Contains(v) 
                })
                .ToList();

            FilterItemsControl.ItemsSource = _currentFilterItems;
            PopupSearchBox.Text = "";
            
            FilterPopup.PlacementTarget = InfoDataGrid;
            FilterPopup.IsOpen = true;
        }

        #endregion

        #region Filter Popup

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string columnName)
            {
                _currentFilterColumn = columnName;
                
                // PERFORMANCE: Use cached values
                var allValues = GetCachedDistinctValues(columnName);
                var selectedValues = _selectedFilters.TryGetValue(columnName, out var set) ? set : new HashSet<string>();

                _currentFilterItems = allValues
                    .Select(v => new FilterItem 
                    { 
                        Value = v, 
                        IsSelected = selectedValues.Contains(v) 
                    })
                    .ToList();

                FilterItemsControl.ItemsSource = _currentFilterItems;
                PopupSearchBox.Text = "";
                
                FilterPopup.PlacementTarget = button;
                FilterPopup.IsOpen = true;
            }
        }

        private void PopupSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = PopupSearchBox.Text?.Trim() ?? "";
            
            if (string.IsNullOrEmpty(searchText))
            {
                FilterItemsControl.ItemsSource = _currentFilterItems;
            }
            else
            {
                FilterItemsControl.ItemsSource = _currentFilterItems
                    .Where(f => f.Value.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _currentFilterItems)
                item.IsSelected = true;
        }

        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _currentFilterItems)
                item.IsSelected = false;
        }

        private void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _selectedFilters[_currentFilterColumn] = new HashSet<string>(
                _currentFilterItems.Where(f => f.IsSelected).Select(f => f.Value));

            FilterPopup.IsOpen = false;
            InvalidateFilterCache();
            _collectionView?.Refresh();
            UpdateCounts();
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            InitializeFilterSets();
            InvalidateFilterCache();
            _collectionView?.Refresh();
            UpdateCounts();
            StatusText.Text = "Filters cleared";
        }

        #endregion

        #region Header Buttons

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var filteredData = _collectionView?.Cast<ElementInfo>().ToList();
            
            if (filteredData == null || filteredData.Count == 0)
            {
                MessageBox.Show("No data to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName = $"ElementInfo_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    ExportToCsv(saveDialog.FileName, filteredData);
                    StatusText.Text = $"Exported {filteredData.Count} row(s)";
                    MessageBox.Show($"Exported successfully!\n\n{saveDialog.FileName}", 
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed: {ex.Message}", 
                        "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ExportToCsv(string filePath, List<ElementInfo> data)
        {
            var sb = new StringBuilder();
            
            var headers = new List<string> { "ID", "Family Name", "Family Type", "Category", "Workset", "Created By", "Edited By" };
            headers.AddRange(_addedParamColumns);
            sb.AppendLine(string.Join(",", headers));
            
            foreach (var info in data)
            {
                var row = new List<string>
                {
                    $"\"{info.Id}\"",
                    $"\"{EscapeCsv(info.FamilyName)}\"",
                    $"\"{EscapeCsv(info.FamilyType)}\"",
                    $"\"{EscapeCsv(info.Category)}\"",
                    $"\"{EscapeCsv(info.Workset)}\"",
                    $"\"{EscapeCsv(info.CreatedBy)}\"",
                    $"\"{EscapeCsv(info.EditedBy)}\""
                };
                
                foreach (var paramName in _addedParamColumns)
                {
                    row.Add($"\"{EscapeCsv(info.GetParameterValue(paramName))}\"");
                }
                
                sb.AppendLine(string.Join(",", row));
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\"", "\"\"");
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _refreshCallback?.Invoke();
            StatusText.Text = "Refreshing...";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion
    }
}
