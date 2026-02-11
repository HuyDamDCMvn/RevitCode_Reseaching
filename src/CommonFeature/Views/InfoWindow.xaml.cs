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
        
        // Filter states
        private HashSet<string> _selectedFamilyNames = new();
        private HashSet<string> _selectedFamilyTypes = new();
        private HashSet<string> _selectedCategories = new();
        private HashSet<string> _selectedWorksets = new();
        
        // Current filter column
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

            // Text filters
            var idFilter = IdFilterTextBox?.Text?.Trim() ?? "";
            var familyNameFilter = FamilyNameFilterTextBox?.Text?.Trim() ?? "";
            var familyTypeFilter = FamilyTypeFilterTextBox?.Text?.Trim() ?? "";
            var categoryFilter = CategoryFilterTextBox?.Text?.Trim() ?? "";
            var worksetFilter = WorksetFilterTextBox?.Text?.Trim() ?? "";

            // Check text filters (case-insensitive contains)
            if (!string.IsNullOrEmpty(idFilter) && 
                !info.Id.ToString().Contains(idFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(familyNameFilter) && 
                !info.FamilyName.Contains(familyNameFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(familyTypeFilter) && 
                !info.FamilyType.Contains(familyTypeFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(categoryFilter) && 
                !info.Category.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase))
                return false;

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

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _collectionView?.Refresh();
            UpdateCounts();
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

        private void FamilyNameFilterButton_Click(object sender, RoutedEventArgs e)
        {
            var allValues = _allElementInfos.Select(x => x.FamilyName).Distinct();
            ShowFilterPopup(sender as Button, "FamilyName", _selectedFamilyNames, allValues);
        }

        private void FamilyTypeFilterButton_Click(object sender, RoutedEventArgs e)
        {
            var allValues = _allElementInfos.Select(x => x.FamilyType).Distinct();
            ShowFilterPopup(sender as Button, "FamilyType", _selectedFamilyTypes, allValues);
        }

        private void CategoryFilterButton_Click(object sender, RoutedEventArgs e)
        {
            var allValues = _allElementInfos.Select(x => x.Category).Distinct();
            ShowFilterPopup(sender as Button, "Category", _selectedCategories, allValues);
        }

        private void WorksetFilterButton_Click(object sender, RoutedEventArgs e)
        {
            var allValues = _allElementInfos.Select(x => x.Workset).Distinct();
            ShowFilterPopup(sender as Button, "Workset", _selectedWorksets, allValues);
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
            IdFilterTextBox.Text = "";
            FamilyNameFilterTextBox.Text = "";
            FamilyTypeFilterTextBox.Text = "";
            CategoryFilterTextBox.Text = "";
            WorksetFilterTextBox.Text = "";
            
            // Reset multi-select filters
            InitializeFilterSets();
            
            _collectionView?.Refresh();
            UpdateCounts();
            StatusText.Text = "Filters cleared";
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
