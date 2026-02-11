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
        
        // Multi-select filter sets
        private Dictionary<string, HashSet<string>> _selectedFilters = new()
        {
            { "Id", new HashSet<string>() },
            { "FamilyName", new HashSet<string>() },
            { "FamilyType", new HashSet<string>() },
            { "Category", new HashSet<string>() },
            { "Workset", new HashSet<string>() },
            { "CreatedBy", new HashSet<string>() },
            { "EditedBy", new HashSet<string>() }
        };
        
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
            _selectedFilters["Id"] = new HashSet<string>(_allElementInfos.Select(e => e.Id.ToString()).Distinct());
            _selectedFilters["FamilyName"] = new HashSet<string>(_allElementInfos.Select(e => e.FamilyName).Distinct());
            _selectedFilters["FamilyType"] = new HashSet<string>(_allElementInfos.Select(e => e.FamilyType).Distinct());
            _selectedFilters["Category"] = new HashSet<string>(_allElementInfos.Select(e => e.Category).Distinct());
            _selectedFilters["Workset"] = new HashSet<string>(_allElementInfos.Select(e => e.Workset).Distinct());
            _selectedFilters["CreatedBy"] = new HashSet<string>(_allElementInfos.Select(e => e.CreatedBy).Distinct());
            _selectedFilters["EditedBy"] = new HashSet<string>(_allElementInfos.Select(e => e.EditedBy).Distinct());
        }

        private bool FilterPredicate(object obj)
        {
            if (obj is not ElementInfo info) return false;

            if (!_selectedFilters["Id"].Contains(info.Id.ToString())) return false;
            if (!_selectedFilters["FamilyName"].Contains(info.FamilyName)) return false;
            if (!_selectedFilters["FamilyType"].Contains(info.FamilyType)) return false;
            if (!_selectedFilters["Category"].Contains(info.Category)) return false;
            if (!_selectedFilters["Workset"].Contains(info.Workset)) return false;
            if (!_selectedFilters["CreatedBy"].Contains(info.CreatedBy)) return false;
            if (!_selectedFilters["EditedBy"].Contains(info.EditedBy)) return false;

            return true;
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

        #region Filter Popup

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string columnName)
            {
                _currentFilterColumn = columnName;
                
                // Get all distinct values for this column
                IEnumerable<string> allValues = columnName switch
                {
                    "Id" => _allElementInfos.Select(x => x.Id.ToString()).Distinct(),
                    "FamilyName" => _allElementInfos.Select(x => x.FamilyName).Distinct(),
                    "FamilyType" => _allElementInfos.Select(x => x.FamilyType).Distinct(),
                    "Category" => _allElementInfos.Select(x => x.Category).Distinct(),
                    "Workset" => _allElementInfos.Select(x => x.Workset).Distinct(),
                    "CreatedBy" => _allElementInfos.Select(x => x.CreatedBy).Distinct(),
                    "EditedBy" => _allElementInfos.Select(x => x.EditedBy).Distinct(),
                    _ => Enumerable.Empty<string>()
                };

                var selectedValues = _selectedFilters[columnName];

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
            
            // Header
            sb.AppendLine("ID,Family Name,Family Type,Category,Workset,Created By,Edited By");
            
            // Data rows
            foreach (var info in data)
            {
                sb.AppendLine($"\"{info.Id}\",\"{EscapeCsv(info.FamilyName)}\",\"{EscapeCsv(info.FamilyType)}\",\"{EscapeCsv(info.Category)}\",\"{EscapeCsv(info.Workset)}\",\"{EscapeCsv(info.CreatedBy)}\",\"{EscapeCsv(info.EditedBy)}\"");
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
