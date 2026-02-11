using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CommonFeature.Models;
using Microsoft.Win32;

namespace CommonFeature.Views
{
    /// <summary>
    /// Code-behind for InfoWindow.xaml
    /// </summary>
    public partial class InfoWindow : Window
    {
        private List<ElementInfo> _allElementInfos;
        private Action _refreshCallback;

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
            
            InfoDataGrid.ItemsSource = _allElementInfos;
            UpdateCounts();
        }

        private void UpdateCounts()
        {
            int totalCount = _allElementInfos?.Count ?? 0;
            CountText.Text = $"{totalCount} element(s) total";
            StatusText.Text = $"Showing all {totalCount} element(s)";
        }

        #region Header Buttons

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_allElementInfos == null || _allElementInfos.Count == 0)
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
                    ExportToCsv(saveDialog.FileName, _allElementInfos);
                    StatusText.Text = $"Exported {_allElementInfos.Count} row(s)";
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
