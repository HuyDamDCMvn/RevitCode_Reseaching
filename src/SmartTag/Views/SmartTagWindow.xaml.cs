using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartTag
{
    /// <summary>
    /// Interaction logic for SmartTagWindow.xaml
    /// Code-behind: only wiring (DataContext, window lifecycle) - no business logic.
    /// </summary>
    public partial class SmartTagWindow : Window
    {
        public SmartTagWindow()
        {
            InitializeComponent();

            // Auto-refresh categories when window loads
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Trigger category refresh on load
            if (DataContext is SmartTagViewModel vm)
            {
                vm.RefreshCategoriesCommand.Execute(null);
                vm.LoadDimensionTypesCommand.Execute(null);
            }
        }
    }

    /// <summary>
    /// Converter that returns Visible if value is not null, Collapsed otherwise.
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
