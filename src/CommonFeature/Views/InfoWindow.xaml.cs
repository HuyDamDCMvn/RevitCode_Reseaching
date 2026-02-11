using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        
        // Filter text values (from column header TextBoxes)
        private Dictionary<string, string> _textFilters = new()
        {
            { "Id", "" },
            { "FamilyName", "" },
            { "FamilyType", "" },
            { "Category", "" },
            { "Workset", "" }
        };
        
        // Multi-select filter sets
        private HashSet<string> _selectedFamilyNames = new();
        private HashSet<string> _selectedFamilyTypes = new();
        private HashSet<string> _selectedCategories = new();
        private HashSet<string> _selectedWorksets = new();
        
        // Current filter column for popup
        private string _currentFilterColumn;
        private List<FilterItem> _currentFilterItems = new();

        public InfoWindow()
        {
            InitializeComponent();
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

        private void InitializeFilterSets()
        {
            _selectedFamilyNames = new HashSet<string>(_allElementInfos.Select(e => e.FamilyName).Distinct());
            _selectedFamilyTypes = new HashSet<string>(_allElementInfos.Select(e => e.FamilyType).Distinct());
            _selectedCategories = new HashSet<string>(_allElementInfos.Select(e => e.Category).Distinct());
            _selectedWorksets = new HashSet<string>(_allElementInfos.Select(e => e.Workset).Distinct());
        }

        private bool FilterPredicate(object obj)
        {
            if (obj is not ElementInfo info) return false;

            // Check text filters (case-insensitive contains)
            var idFilter = _textFilters["Id"];
            if (!string.IsNullOrEmpty(idFilter) && 
                !info.Id.ToString().Contains(idFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            var familyNameFilter = _textFilters["FamilyName"];
            if (!string.IsNullOrEmpty(familyNameFilter) && 
                !info.FamilyName.Contains(familyNameFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            var familyTypeFilter = _textFilters["FamilyType"];
            if (!string.IsNullOrEmpty(familyTypeFilter) && 
                !info.FamilyType.Contains(familyTypeFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            var categoryFilter = _textFilters["Category"];
            if (!string.IsNullOrEmpty(categoryFilter) && 
                !info.Category.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            var worksetFilter = _textFilters["Workset"];
            if (!string.IsNullOrEmpty(worksetFilter) && 
                !info.Workset.Contains(worksetFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            // Check multi-select filters
            if (!_selectedFamilyNames.Contains(info.FamilyName)) return false;
            if (!_selectedFamilyTypes.Contains(info.FamilyType)) return false;
            if (!_selectedCategories.Contains(info.Category)) return false;
            if (!_selectedWorksets.Contains(info.Workset)) return false;

            return true;
        }

        private void UpdateCounts()
        {
            int totalCount = _allElementInfos?.Count ?? 0;
            int filteredCount = _collectionView?.Cast<object>().Count() ?? 0;

            CountText.Text = $"{totalCount} element(s) total";
            
            if (filteredCount < totalCount)
            {
                FilterStatusText.Text = $"Showing {filteredCount} of {totalCount}";
                StatusText.Text = "Filter applied";
            }
            else
            {
                FilterStatusText.Text = "";
                StatusText.Text = $"Showing all {totalCount} element(s)";
            }
        }

        /// <summary>
        /// Handle TextChanged from column header filter TextBoxes
        /// </summary>
        private void ColumnFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && textBox.Tag is string columnName)
            {
                _textFilters[columnName] = textBox.Text?.Trim() ?? "";
                _collectionView?.Refresh();
                UpdateCounts();
            }
        }

        /// <summary>
        /// Handle Click from column header filter dropdown buttons
        /// </summary>
        private void ColumnFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string columnName)
            {
                IEnumerable<string> allValues;
                HashSet<string> selectedValues;

                switch (columnName)
                {
                    case "FamilyName":
                        allValues = _allElementInfos.Select(x => x.FamilyName).Distinct();
                        selectedValues = _selectedFamilyNames;
                        break;
                    case "FamilyType":
                        allValues = _allElementInfos.Select(x => x.FamilyType).Distinct();
                        selectedValues = _selectedFamilyTypes;
                        break;
                    case "Category":
                        allValues = _allElementInfos.Select(x => x.Category).Distinct();
                        selectedValues = _selectedCategories;
                        break;
                    case "Workset":
                        allValues = _allElementInfos.Select(x => x.Workset).Distinct();
                        selectedValues = _selectedWorksets;
                        break;
                    default:
                        return;
                }

                ShowFilterPopup(button, columnName, selectedValues, allValues);
            }
        }

        #region Multi-Select Filter Popup

        private void ShowFilterPopup(Button button, string column, HashSet<string> selectedValues, IEnumerable<string> allValues)
        {
            _currentFilterColumn = column;
            
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

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // Update happens via binding
        }

        private void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
        {
            // Update the corresponding filter set
            var selected = new HashSet<string>(_currentFilterItems.Where(f => f.IsSelected).Select(f => f.Value));
            
            switch (_currentFilterColumn)
            {
                case "FamilyName":
                    _selectedFamilyNames = selected;
                    break;
                case "FamilyType":
                    _selectedFamilyTypes = selected;
                    break;
                case "Category":
                    _selectedCategories = selected;
                    break;
                case "Workset":
                    _selectedWorksets = selected;
                    break;
            }

            FilterPopup.IsOpen = false;
            _collectionView?.Refresh();
            UpdateCounts();
        }

        #endregion

        #region Header Buttons

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear text filters
            foreach (var key in _textFilters.Keys.ToList())
            {
                _textFilters[key] = "";
            }
            
            // Clear TextBoxes in column headers (find them in visual tree)
            ClearColumnHeaderTextBoxes(InfoDataGrid);
            
            // Reset multi-select filters
            InitializeFilterSets();
            
            _collectionView?.Refresh();
            UpdateCounts();
            StatusText.Text = "Filters cleared";
        }

        private void ClearColumnHeaderTextBoxes(DependencyObject parent)
        {
            if (parent == null) return;

            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                
                if (child is TextBox textBox && textBox.Tag is string)
                {
                    textBox.Text = "";
                }
                else
                {
                    ClearColumnHeaderTextBoxes(child);
                }
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            // Export filtered data
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
            
            // Header
            sb.AppendLine("ID,Family Name,Family Type,Category,Workset");
            
            // Data rows
            foreach (var info in data)
            {
                sb.AppendLine($"\"{info.Id}\",\"{EscapeCsv(info.FamilyName)}\",\"{EscapeCsv(info.FamilyType)}\",\"{EscapeCsv(info.Category)}\",\"{EscapeCsv(info.Workset)}\"");
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
