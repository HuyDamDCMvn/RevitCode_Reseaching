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

    /// <summary>
    /// Code-behind for InfoWindow.xaml
    /// </summary>
    public partial class InfoWindow : Window
    {
        private List<ElementInfo> _allElementInfos;
        private ICollectionView _collectionView;
        private Action _refreshCallback;
        
        // Multi-select filter sets - stores currently selected values for each column
        private Dictionary<string, HashSet<string>> _selectedFilters = new();
        
        // Current filter column for popup
        private string _currentFilterColumn;
        private List<FilterItem> _currentFilterItems = new();
        
        // Parameter selection
        private List<ParameterInfo> _availableParameters = new();
        private List<string> _addedParamColumns = new();
        
        // Track parameter metadata (isInstance)
        private Dictionary<string, bool> _parameterIsInstance = new();
        
        // Callback to request parameters from Revit (synchronous, used during data loading)
        public Func<List<long>, List<ParameterInfo>> GetParametersCallback { get; set; }
        public Func<List<long>, List<string>, Dictionary<long, ParameterValuesResult>> GetParameterValuesCallback { get; set; }
        
        // ExternalEvent for update operations (asynchronous, Revit-safe)
        public Action<List<ParameterUpdateItem>> RaiseUpdateEvent { get; set; }

        // Light green color for modified cells
        private static readonly SolidColorBrush ModifiedCellBrush = new(Color.FromRgb(200, 230, 201)); // Light green

        // Fixed column names
        private static readonly string[] FixedColumns = { "Id", "FamilyName", "FamilyType", "Category", "Workset", "CreatedBy", "EditedBy" };

        public InfoWindow()
        {
            InitializeComponent();
        }

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
            
            // Reset cell backgrounds and update UI
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
            
            // Setup collection view for filtering
            _collectionView = CollectionViewSource.GetDefaultView(_allElementInfos);
            _collectionView.Filter = FilterPredicate;
            
            InfoDataGrid.ItemsSource = _collectionView;
            UpdateCounts();
        }

        /// <summary>
        /// Initialize filter sets with ALL values selected (for clear filter)
        /// </summary>
        private void InitializeFilterSets()
        {
            // Clear and reinitialize all filters
            _selectedFilters.Clear();
            
            // Fixed columns - select ALL values
            _selectedFilters["Id"] = new HashSet<string>(_allElementInfos.Select(e => e.Id.ToString()).Distinct());
            _selectedFilters["FamilyName"] = new HashSet<string>(_allElementInfos.Select(e => e.FamilyName).Distinct());
            _selectedFilters["FamilyType"] = new HashSet<string>(_allElementInfos.Select(e => e.FamilyType).Distinct());
            _selectedFilters["Category"] = new HashSet<string>(_allElementInfos.Select(e => e.Category).Distinct());
            _selectedFilters["Workset"] = new HashSet<string>(_allElementInfos.Select(e => e.Workset).Distinct());
            _selectedFilters["CreatedBy"] = new HashSet<string>(_allElementInfos.Select(e => e.CreatedBy).Distinct());
            _selectedFilters["EditedBy"] = new HashSet<string>(_allElementInfos.Select(e => e.EditedBy).Distinct());
            
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

            // Check fixed column filters
            if (_selectedFilters.TryGetValue("Id", out var idFilter) && idFilter.Count > 0)
            {
                if (!idFilter.Contains(info.Id.ToString())) return false;
            }
            if (_selectedFilters.TryGetValue("FamilyName", out var fnFilter) && fnFilter.Count > 0)
            {
                if (!fnFilter.Contains(info.FamilyName)) return false;
            }
            if (_selectedFilters.TryGetValue("FamilyType", out var ftFilter) && ftFilter.Count > 0)
            {
                if (!ftFilter.Contains(info.FamilyType)) return false;
            }
            if (_selectedFilters.TryGetValue("Category", out var catFilter) && catFilter.Count > 0)
            {
                if (!catFilter.Contains(info.Category)) return false;
            }
            if (_selectedFilters.TryGetValue("Workset", out var wsFilter) && wsFilter.Count > 0)
            {
                if (!wsFilter.Contains(info.Workset)) return false;
            }
            if (_selectedFilters.TryGetValue("CreatedBy", out var cbFilter) && cbFilter.Count > 0)
            {
                if (!cbFilter.Contains(info.CreatedBy)) return false;
            }
            if (_selectedFilters.TryGetValue("EditedBy", out var ebFilter) && ebFilter.Count > 0)
            {
                if (!ebFilter.Contains(info.EditedBy)) return false;
            }
            
            // Check parameter column filters
            foreach (var paramName in _addedParamColumns)
            {
                if (_selectedFilters.TryGetValue(paramName, out var paramFilter) && paramFilter.Count > 0)
                {
                    var value = info.GetParameterValue(paramName);
                    if (!paramFilter.Contains(value)) return false;
                }
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
                StatusText.Text = $"{modifiedCount} value(s) modified - Click 'Update Value' to apply";
            }
        }

        #region Cell Editing

        private void InfoDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            // Only allow editing for parameter columns (not the fixed columns)
            var column = e.Column;
            if (column == null) return;
            
            // Get column header text
            var headerText = GetColumnHeaderText(column);
            
            // Check if this is a parameter column (contains "(I)" or "(T)")
            if (!headerText.Contains("(I)") && !headerText.Contains("(T)"))
            {
                e.Cancel = true;
                return;
            }
            
            // Extract parameter name
            var paramName = headerText;
            if (paramName.EndsWith(" (I)"))
                paramName = paramName[..^4];
            else if (paramName.EndsWith(" (T)"))
                paramName = paramName[..^4];
            
            // Check if this element's parameter is read-only
            if (e.Row.Item is ElementInfo elementInfo)
            {
                if (elementInfo.IsParameterReadOnly(paramName))
                {
                    e.Cancel = true;
                    StatusText.Text = $"Cannot edit value of BuiltInParameter or read-only parameter '{paramName}'";
                    return;
                }
            }
        }

        private void InfoDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Cancel) return;
            
            if (e.Row.Item is not ElementInfo elementInfo) return;
            if (e.EditingElement is not TextBox textBox) return;
            
            var column = e.Column;
            var headerText = GetColumnHeaderText(column);
            
            // Extract parameter name (remove " (I)" or " (T)" suffix)
            var paramName = headerText;
            if (paramName.EndsWith(" (I)"))
                paramName = paramName[..^4];
            else if (paramName.EndsWith(" (T)"))
                paramName = paramName[..^4];
            
            var newValue = textBox.Text;
            var oldValue = elementInfo.GetParameterValue(paramName);
            
            if (newValue != oldValue)
            {
                elementInfo.SetParameterValue(paramName, newValue);
                
                // Update cell background color
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateCellBackgrounds();
                    UpdateModifiedCount();
                }));
            }
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
                    // Map binding path to display name
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

        private string ExtractParamNameFromPath(string path)
        {
            // Extract "ParamName" from "Parameters[ParamName]"
            if (path.StartsWith("Parameters[") && path.EndsWith("]"))
            {
                var paramName = path.Substring(11, path.Length - 12);
                // Return with (I) or (T) suffix if it's a parameter column
                if (_parameterIsInstance.TryGetValue(paramName, out var isInstance))
                {
                    return $"{paramName} {(isInstance ? "(I)" : "(T)")}";
                }
                return paramName;
            }
            return path;
        }

        private void UpdateCellBackgrounds()
        {
            foreach (var item in InfoDataGrid.Items)
            {
                if (item is not ElementInfo elementInfo) continue;
                
                var row = InfoDataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                if (row == null) continue;
                
                foreach (var col in InfoDataGrid.Columns)
                {
                    var headerText = GetColumnHeaderText(col);
                    var paramName = headerText;
                    if (paramName.EndsWith(" (I)"))
                        paramName = paramName[..^4];
                    else if (paramName.EndsWith(" (T)"))
                        paramName = paramName[..^4];
                    
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
        }

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
                // Create a copy of ModifiedParameters to avoid modification during iteration
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
                $"Do you want to update {updates.Count} parameter value(s) in the Revit model?\n\n" +
                "Note: This will modify the Revit model. Make sure to save your work before proceeding.",
                "Confirm Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result != MessageBoxResult.Yes) return;
            
            if (RaiseUpdateEvent != null)
            {
                // Disable button during update
                UpdateValueButton.IsEnabled = false;
                StatusText.Text = "Updating values... (waiting for Revit)";
                
                // Raise ExternalEvent to run update on Revit thread
                // The handler will call OnUpdateCompleted() when done
                RaiseUpdateEvent(updates);
            }
            else
            {
                MessageBox.Show("Update event not available. Please close and reopen the window.", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Add Parameter

        private void AddParamButton_Click(object sender, RoutedEventArgs e)
        {
            // Get selected rows in DataGrid
            var selectedItems = InfoDataGrid.SelectedCells
                .Select(c => c.Item)
                .OfType<ElementInfo>()
                .Distinct()
                .ToList();
            
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("Please select at least one row in the table to get available parameters.", 
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get element IDs
            var elementIds = selectedItems.Select(x => x.Id).ToList();

            // Request parameters from Revit via callback
            if (GetParametersCallback != null)
            {
                _availableParameters = GetParametersCallback(elementIds);
                
                // Exclude already added parameters
                _availableParameters = _availableParameters
                    .Where(p => !_addedParamColumns.Contains(p.Name))
                    .ToList();
                
                if (_availableParameters.Count == 0)
                {
                    MessageBox.Show("No additional parameters available for selected elements.", 
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

            // Get all element IDs
            var elementIds = _allElementInfos.Select(x => x.Id).ToList();
            var paramNames = selectedParams.Select(p => p.Name).ToList();

            // Get parameter values from Revit
            if (GetParameterValuesCallback != null)
            {
                var paramValues = GetParameterValuesCallback(elementIds, paramNames);

                // Update ElementInfo with parameter values and read-only status
                foreach (var info in _allElementInfos)
                {
                    if (paramValues.TryGetValue(info.Id, out var result))
                    {
                        foreach (var kvp in result.Values)
                        {
                            info.Parameters[kvp.Key] = kvp.Value;
                            // Store original value
                            info.OriginalParameters[kvp.Key] = kvp.Value;
                        }
                        
                        // Store read-only status
                        foreach (var readOnlyParam in result.ReadOnlyParams)
                        {
                            info.ReadOnlyParameters.Add(readOnlyParam);
                        }
                    }
                }

                // Add columns to DataGrid and initialize filters
                foreach (var param in selectedParams)
                {
                    AddParameterColumn(param.Name, param.IsInstance);
                    _addedParamColumns.Add(param.Name);
                    _parameterIsInstance[param.Name] = param.IsInstance;
                    
                    // Initialize filter for this parameter - select ALL values
                    _selectedFilters[param.Name] = new HashSet<string>(
                        _allElementInfos.Select(e => e.GetParameterValue(param.Name)).Distinct());
                }

                // Refresh DataGrid
                InfoDataGrid.Items.Refresh();
                
                StatusText.Text = $"Added {selectedParams.Count} parameter column(s)";
            }

            ParamPopup.IsOpen = false;
        }

        private void AddParameterColumn(string paramName, bool isInstance)
        {
            var headerText = $"{paramName} {(isInstance ? "(I)" : "(T)")}";
            
            // Create the header content programmatically with event handler
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
            
            // Create editable text column with header content
            var column = new DataGridTextColumn
            {
                Header = headerGrid,
                Binding = new Binding($"Parameters[{paramName}]")
                {
                    Mode = BindingMode.TwoWay,
                    TargetNullValue = "-",
                    FallbackValue = "-",
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
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
            // Find and remove the column
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
                
                // Clear parameter values from ElementInfo
                foreach (var info in _allElementInfos)
                {
                    info.Parameters.Remove(paramName);
                    info.OriginalParameters.Remove(paramName);
                    info.ModifiedParameters.Remove(paramName);
                }
                
                _collectionView?.Refresh();
                UpdateCounts();
                StatusText.Text = $"Removed column '{paramName}'";
            }
        }

        private string GetParamNameFromColumn(DataGridColumn col)
        {
            // Check if header is a Grid (programmatically created parameter column)
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
            // Check if header is a string (legacy format)
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
            // Check if right-click is on a column header
            var dep = (DependencyObject)e.OriginalSource;
            while (dep != null && !(dep is DataGridColumnHeader))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }
            
            if (dep is DataGridColumnHeader header && header.Column != null)
            {
                // Try to get param name from column (handles both Grid and string headers)
                var paramName = GetParamNameFromColumn(header.Column);
                
                if (!string.IsNullOrEmpty(paramName) && _addedParamColumns.Contains(paramName))
                {
                    // Show context menu for parameter columns
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
                }
            }
        }

        private void ShowFilterForParameter(string paramName)
        {
            _currentFilterColumn = paramName;
            
            // Get distinct values from CURRENTLY VISIBLE elements only
            var visibleElements = GetFilteredElements();
            var allValues = visibleElements
                .Select(x => x.GetParameterValue(paramName))
                .Distinct();

            var selectedValues = _selectedFilters.TryGetValue(paramName, out var set) ? set : new HashSet<string>();

            // Create filter items
            _currentFilterItems = allValues
                .OrderBy(v => v)
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
                
                // Get distinct values from CURRENTLY VISIBLE elements only
                var visibleElements = GetFilteredElements();
                
                IEnumerable<string> allValues = columnName switch
                {
                    "Id" => visibleElements.Select(x => x.Id.ToString()).Distinct(),
                    "FamilyName" => visibleElements.Select(x => x.FamilyName).Distinct(),
                    "FamilyType" => visibleElements.Select(x => x.FamilyType).Distinct(),
                    "Category" => visibleElements.Select(x => x.Category).Distinct(),
                    "Workset" => visibleElements.Select(x => x.Workset).Distinct(),
                    "CreatedBy" => visibleElements.Select(x => x.CreatedBy).Distinct(),
                    "EditedBy" => visibleElements.Select(x => x.EditedBy).Distinct(),
                    _ => visibleElements.Select(x => x.GetParameterValue(columnName)).Distinct()
                };

                var selectedValues = _selectedFilters.TryGetValue(columnName, out var set) ? set : new HashSet<string>();

                // Create filter items
                _currentFilterItems = allValues
                    .OrderBy(v => v)
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
            // Update the corresponding filter set
            _selectedFilters[_currentFilterColumn] = new HashSet<string>(
                _currentFilterItems.Where(f => f.IsSelected).Select(f => f.Value));

            FilterPopup.IsOpen = false;
            _collectionView?.Refresh();
            UpdateCounts();
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset all filters to include ALL values
            InitializeFilterSets();
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
                    MessageBox.Show($"Data exported successfully!\n\n{saveDialog.FileName}", 
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
            
            // Build header with dynamic columns
            var headers = new List<string> { "ID", "Family Name", "Family Type", "Category", "Workset", "Created By", "Edited By" };
            headers.AddRange(_addedParamColumns);
            sb.AppendLine(string.Join(",", headers));
            
            // Data rows
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
                
                // Add dynamic parameter values
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
