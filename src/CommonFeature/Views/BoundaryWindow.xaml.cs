using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.DB;
using CommonFeature.Models;

namespace CommonFeature.Views
{
    /// <summary>
    /// Modeless window for visualizing element bounding boxes and key points.
    /// 
    /// Features:
    /// - Display world-aligned or rotated bounding boxes
    /// - Show min/max/centroid points as colored spheres  
    /// - Auto-updates when Revit selection changes
    /// - Customizable colors and sizes
    /// 
    /// Architecture:
    /// - This is purely a UI class - no Revit API calls
    /// - All Revit operations go through callbacks set by CommonFeatureHandler
    /// - Selection monitoring via DispatcherTimer (polls Revit selection)
    /// </summary>
    public partial class BoundaryWindow : Window
    {
        #region Fields

        private readonly List<long> _selectedElementIds = new();
        
        // Color settings (WPF colors, converted to Revit colors when needed)
        private System.Windows.Media.Color _boundingBoxColor = System.Windows.Media.Color.FromRgb(33, 150, 243); // #2196F3 Blue
        private System.Windows.Media.Color _minPointColor = System.Windows.Media.Color.FromRgb(244, 67, 54);     // #F44336 Red
        private System.Windows.Media.Color _maxPointColor = System.Windows.Media.Color.FromRgb(76, 175, 80);     // #4CAF50 Green
        private System.Windows.Media.Color _centroidColor = System.Windows.Media.Color.FromRgb(255, 193, 7);     // #FFC107 Amber
        
        // Selection auto-update timer
        private System.Windows.Threading.DispatcherTimer _selectionTimer;
        private string _lastSelectionHash = "";
        
        #endregion

        #region Callbacks
        
        /// <summary>
        /// These callbacks are set by CommonFeatureHandler to bridge UI and Revit API.
        /// This pattern keeps BoundaryWindow free of direct Revit API dependencies.
        /// </summary>

        /// <summary>
        /// Callback to get current selection from Revit (cached, thread-safe)
        /// </summary>
        public Func<List<long>> GetCurrentSelectionCallback { get; set; }

        /// <summary>
        /// Call to refresh cached selection via ExternalEvent before reading.
        /// </summary>
        public Action RefreshSelectionRequested { get; set; }

        /// <summary>
        /// Callback to pick elements in Revit (runs via ExternalEvent)
        /// </summary>
        public Action PickElementsCallback { get; set; }

        /// <summary>
        /// Callback to update graphics preview
        /// </summary>
        public Action<BoundaryDisplaySettings> UpdatePreviewCallback { get; set; }

        /// <summary>
        /// Callback to clear preview
        /// </summary>
        public Action ClearPreviewCallback { get; set; }

        /// <summary>
        /// Callback to get view name for detecting view change
        /// </summary>
        public Func<string> GetViewNameCallback { get; set; }

        #endregion

        #region Constructor

        public BoundaryWindow()
        {
            InitializeComponent();
            
            // Initialize with current selection if available
            Loaded += BoundaryWindow_Loaded;
        }

        #endregion

        #region Window Events

        private void BoundaryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Try to get current selection
            LoadCurrentSelection();
            
            // Start auto-update timer for selection changes
            StartSelectionMonitoring();
        }
        
        private void StartSelectionMonitoring()
        {
            _selectionTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500) // Check every 500ms
            };
            _selectionTimer.Tick += SelectionTimer_Tick;
            _selectionTimer.Start();
        }
        
        private void SelectionTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                RefreshSelectionRequested?.Invoke();
                var currentSelection = GetCurrentSelectionCallback?.Invoke();
                if (currentSelection == null) return;
                
                // Create hash to compare
                var newHash = string.Join(",", currentSelection.OrderBy(x => x));
                
                if (newHash != _lastSelectionHash)
                {
                    _lastSelectionHash = newHash;
                    
                    // Update selection
                    _selectedElementIds.Clear();
                    _selectedElementIds.AddRange(currentSelection);
                    UpdateSelectionDisplay();
                    
                    // Auto-update preview if any toggle is on
                    UpdatePreviewIfNeeded();
                }
            }
            catch
            {
                // Ignore errors during polling
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Stop selection monitoring timer
            if (_selectionTimer != null)
            {
                _selectionTimer.Stop();
                _selectionTimer.Tick -= SelectionTimer_Tick;
                _selectionTimer = null;
            }
            
            // Clear all preview graphics when window closes
            try
            {
                ClearPreviewCallback?.Invoke();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Set initial selection from pre-selected elements
        /// </summary>
        public void SetInitialSelection(List<long> elementIds)
        {
            if (elementIds == null || elementIds.Count == 0) return;
            
            _selectedElementIds.Clear();
            _selectedElementIds.AddRange(elementIds);
            UpdateSelectionDisplay();
            
            // Auto-update preview if any toggle is on
            UpdatePreviewIfNeeded();
        }

        /// <summary>
        /// Called when element picking completes
        /// </summary>
        public void OnElementsPicked(List<long> elementIds)
        {
            Dispatcher.Invoke(() =>
            {
                _selectedElementIds.Clear();
                if (elementIds != null)
                {
                    _selectedElementIds.AddRange(elementIds);
                }
                UpdateSelectionDisplay();
                UpdatePreviewIfNeeded();
            });
        }

        #endregion

        #region Private Methods

        private void LoadCurrentSelection()
        {
            try
            {
                var selection = GetCurrentSelectionCallback?.Invoke();
                if (selection != null && selection.Count > 0)
                {
                    _selectedElementIds.Clear();
                    _selectedElementIds.AddRange(selection);
                    UpdateSelectionDisplay();
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        private void UpdateSelectionDisplay()
        {
            int count = _selectedElementIds.Count;
            ElementCountText.Text = $"{count} element(s) selected";
            SelectionText.Text = count > 0 
                ? $"{count} element(s) selected" 
                : "No elements selected";
        }

        private void UpdatePreviewIfNeeded()
        {
            // Check if any preview is enabled
            bool anyEnabled = (ShowBoundingBoxToggle.IsChecked == true) ||
                              (ShowMinPointToggle.IsChecked == true) ||
                              (ShowMaxPointToggle.IsChecked == true) ||
                              (ShowCentroidToggle.IsChecked == true);

            if (!anyEnabled || _selectedElementIds.Count == 0)
            {
                ClearPreviewCallback?.Invoke();
                return;
            }

            // Build settings and update preview
            var settings = BuildDisplaySettings();
            UpdatePreviewCallback?.Invoke(settings);
        }

        private BoundaryDisplaySettings BuildDisplaySettings()
        {
            return new BoundaryDisplaySettings
            {
                ElementIds = _selectedElementIds.ToList(),
                ShowBoundingBox = ShowBoundingBoxToggle.IsChecked == true,
                UseRotatedBoundingBox = RotatedAxisRadio.IsChecked == true,
                ShowMinPoint = ShowMinPointToggle.IsChecked == true,
                ShowMaxPoint = ShowMaxPointToggle.IsChecked == true,
                ShowCentroid = ShowCentroidToggle.IsChecked == true,
                BoundingBoxColor = ConvertToRevitColor(_boundingBoxColor),
                MinPointColor = ConvertToRevitColor(_minPointColor),
                MaxPointColor = ConvertToRevitColor(_maxPointColor),
                CentroidColor = ConvertToRevitColor(_centroidColor),
                LineThickness = (int)LineThicknessSlider.Value,
                SphereDiameterMm = (int)SphereDiameterSlider.Value
            };
        }

        private static Autodesk.Revit.DB.Color ConvertToRevitColor(System.Windows.Media.Color wpfColor)
        {
            return new Autodesk.Revit.DB.Color(wpfColor.R, wpfColor.G, wpfColor.B);
        }

        /// <summary>
        /// Show a simple color picker dialog using predefined colors
        /// </summary>
        private System.Windows.Media.Color? ShowColorPickerDialog(System.Windows.Media.Color currentColor)
        {
            // Create a simple color picker window
            var pickerWindow = new Window
            {
                Title = "Select Color",
                Width = 320,
                Height = 280,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            System.Windows.Media.Color? selectedColor = null;
            
            // Predefined colors
            var colors = new[]
            {
                // Row 1 - Primary
                System.Windows.Media.Color.FromRgb(244, 67, 54),   // Red
                System.Windows.Media.Color.FromRgb(233, 30, 99),   // Pink
                System.Windows.Media.Color.FromRgb(156, 39, 176),  // Purple
                System.Windows.Media.Color.FromRgb(103, 58, 183),  // Deep Purple
                System.Windows.Media.Color.FromRgb(63, 81, 181),   // Indigo
                System.Windows.Media.Color.FromRgb(33, 150, 243),  // Blue
                // Row 2
                System.Windows.Media.Color.FromRgb(3, 169, 244),   // Light Blue
                System.Windows.Media.Color.FromRgb(0, 188, 212),   // Cyan
                System.Windows.Media.Color.FromRgb(0, 150, 136),   // Teal
                System.Windows.Media.Color.FromRgb(76, 175, 80),   // Green
                System.Windows.Media.Color.FromRgb(139, 195, 74),  // Light Green
                System.Windows.Media.Color.FromRgb(205, 220, 57),  // Lime
                // Row 3
                System.Windows.Media.Color.FromRgb(255, 235, 59),  // Yellow
                System.Windows.Media.Color.FromRgb(255, 193, 7),   // Amber
                System.Windows.Media.Color.FromRgb(255, 152, 0),   // Orange
                System.Windows.Media.Color.FromRgb(255, 87, 34),   // Deep Orange
                System.Windows.Media.Color.FromRgb(121, 85, 72),   // Brown
                System.Windows.Media.Color.FromRgb(158, 158, 158), // Gray
                // Row 4
                System.Windows.Media.Color.FromRgb(96, 125, 139),  // Blue Gray
                System.Windows.Media.Color.FromRgb(0, 0, 0),       // Black
                System.Windows.Media.Color.FromRgb(255, 255, 255), // White
                System.Windows.Media.Color.FromRgb(183, 28, 28),   // Dark Red
                System.Windows.Media.Color.FromRgb(13, 71, 161),   // Dark Blue
                System.Windows.Media.Color.FromRgb(27, 94, 32),    // Dark Green
            };

            var mainPanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            
            // Current color preview
            var currentPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            currentPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Current: ", VerticalAlignment = VerticalAlignment.Center });
            var currentPreview = new System.Windows.Controls.Border
            {
                Width = 40,
                Height = 24,
                Background = new SolidColorBrush(currentColor),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2)
            };
            currentPanel.Children.Add(currentPreview);
            mainPanel.Children.Add(currentPanel);

            // Color grid
            var wrapPanel = new System.Windows.Controls.WrapPanel { Width = 288 };
            foreach (var color in colors)
            {
                var colorBtn = new System.Windows.Controls.Button
                {
                    Width = 40,
                    Height = 40,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(color),
                    BorderBrush = color == currentColor 
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 33, 33)) 
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(189, 189, 189)),
                    BorderThickness = color == currentColor ? new Thickness(3) : new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                colorBtn.Click += (s, ev) =>
                {
                    selectedColor = color;
                    pickerWindow.DialogResult = true;
                    pickerWindow.Close();
                };
                wrapPanel.Children.Add(colorBtn);
            }
            mainPanel.Children.Add(wrapPanel);

            // Cancel button
            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            cancelBtn.Click += (s, ev) =>
            {
                pickerWindow.DialogResult = false;
                pickerWindow.Close();
            };
            mainPanel.Children.Add(cancelBtn);

            pickerWindow.Content = mainPanel;
            
            if (pickerWindow.ShowDialog() == true)
            {
                return selectedColor;
            }
            return null;
        }

        private static System.Windows.Media.Color ConvertFromRevitColor(Autodesk.Revit.DB.Color revitColor)
        {
            return System.Windows.Media.Color.FromRgb(revitColor.Red, revitColor.Green, revitColor.Blue);
        }

        #endregion

        #region Event Handlers - Selection

        private void PickElements_Click(object sender, RoutedEventArgs e)
        {
            // Minimize window during picking
            WindowState = WindowState.Minimized;
            PickElementsCallback?.Invoke();
        }

        #endregion

        #region Event Handlers - Bounding Box

        private void BoundingBoxToggle_Changed(object sender, RoutedEventArgs e)
        {
            UpdatePreviewIfNeeded();
        }

        private void BoundingBoxMode_Changed(object sender, RoutedEventArgs e)
        {
            if (ShowBoundingBoxToggle?.IsChecked == true)
            {
                UpdatePreviewIfNeeded();
            }
        }

        private void ChangeBoundingBoxColor_Click(object sender, RoutedEventArgs e)
        {
            var newColor = ShowColorPickerDialog(_boundingBoxColor);
            if (newColor.HasValue)
            {
                _boundingBoxColor = newColor.Value;
                BoundingBoxColorPreview.Background = new SolidColorBrush(_boundingBoxColor);
                
                if (ShowBoundingBoxToggle.IsChecked == true)
                {
                    UpdatePreviewIfNeeded();
                }
            }
        }

        private void LineThickness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LineThicknessValue != null)
            {
                LineThicknessValue.Text = ((int)e.NewValue).ToString();
                
                if (ShowBoundingBoxToggle?.IsChecked == true)
                {
                    UpdatePreviewIfNeeded();
                }
            }
        }

        #endregion

        #region Event Handlers - Points

        private void PointToggle_Changed(object sender, RoutedEventArgs e)
        {
            UpdatePreviewIfNeeded();
        }

        private void ChangePointColor_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            if (button?.Tag == null) return;

            string pointType = button.Tag.ToString();
            System.Windows.Media.Color currentColor;
            
            switch (pointType)
            {
                case "MinPoint":
                    currentColor = _minPointColor;
                    break;
                case "MaxPoint":
                    currentColor = _maxPointColor;
                    break;
                case "Centroid":
                    currentColor = _centroidColor;
                    break;
                default:
                    return;
            }

            var newColor = ShowColorPickerDialog(currentColor);
            if (newColor.HasValue)
            {
                switch (pointType)
                {
                    case "MinPoint":
                        _minPointColor = newColor.Value;
                        MinPointColorPreview.Background = new SolidColorBrush(newColor.Value);
                        break;
                    case "MaxPoint":
                        _maxPointColor = newColor.Value;
                        MaxPointColorPreview.Background = new SolidColorBrush(newColor.Value);
                        break;
                    case "Centroid":
                        _centroidColor = newColor.Value;
                        CentroidColorPreview.Background = new SolidColorBrush(newColor.Value);
                        break;
                }

                UpdatePreviewIfNeeded();
            }
        }

        private void SphereDiameter_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SphereDiameterValue != null)
            {
                SphereDiameterValue.Text = ((int)e.NewValue).ToString();
                
                bool anyPointEnabled = (ShowMinPointToggle?.IsChecked == true) ||
                                        (ShowMaxPointToggle?.IsChecked == true) ||
                                        (ShowCentroidToggle?.IsChecked == true);
                
                if (anyPointEnabled)
                {
                    UpdatePreviewIfNeeded();
                }
            }
        }

        #endregion

        #region Event Handlers - Footer

        private void ClearPreview_Click(object sender, RoutedEventArgs e)
        {
            // Uncheck all toggles
            ShowBoundingBoxToggle.IsChecked = false;
            ShowMinPointToggle.IsChecked = false;
            ShowMaxPointToggle.IsChecked = false;
            ShowCentroidToggle.IsChecked = false;
            
            ClearPreviewCallback?.Invoke();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion
    }
}
