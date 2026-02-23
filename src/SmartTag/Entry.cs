using System;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace SmartTag
{
    /// <summary>
    /// Entry point for pyRevit launcher.
    /// Manages single-instance modeless window.
    /// </summary>
    public static class Entry
    {
        private static SmartTagWindow _window;
        private static ExternalEvent _externalEvent;
        private static SmartTagHandler _handler;

        /// <summary>
        /// Show or focus the SmartTag window.
        /// Called by pyRevit script.
        /// </summary>
        public static void ShowTool(UIApplication uiapp)
        {
            try
            {
                if (uiapp == null)
                {
                    TaskDialog.Show("SmartTag Error", "UIApplication is null.");
                    return;
                }

                if (uiapp.ActiveUIDocument?.Document == null)
                {
                    TaskDialog.Show("SmartTag Error", "No active document. Please open a project first.");
                    return;
                }

                // If window exists and is visible, just activate it
                if (_window != null && _window.IsVisible)
                {
                    _window.Activate();
                    return;
                }

                // Create handler and external event (only once)
                if (_handler == null)
                {
                    _handler = new SmartTagHandler();
                }
                
                if (_externalEvent == null)
                {
                    _externalEvent = ExternalEvent.Create(_handler);
                    _handler.SetExternalEvent(_externalEvent);
                }

                // Create ViewModel and Window
                var viewModel = new SmartTagViewModel(_externalEvent, _handler);
                _window = new SmartTagWindow();
                _window.DataContext = viewModel;

                // Set Revit as owner (keeps window on top of Revit)
                var helper = new WindowInteropHelper(_window);
                helper.Owner = uiapp.MainWindowHandle;

                // Cleanup on close
                _window.Closed += (sender, args) =>
                {
                    viewModel.Cleanup();
                    _window = null;
                };

                // Show modeless (non-blocking)
                _window.Show();

                // Queue category load immediately (same thread as Revit caller) so Revit processes it
                // when control returns - avoids user having to press Refresh on first open
                try
                {
                    viewModel.RefreshCategoriesCommand.Execute(null);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Initial RefreshCategories: {ex.Message}");
                }

                // Defer dimension types load so UI is ready
                _window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        viewModel.LoadDimensionTypesCommand?.Execute(null);
                    }
                    catch (Exception initEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Initial LoadDimensionTypes: {initEx.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("SmartTag Error", 
                    $"Failed to open window:\n\n{ex.Message}");
            }
        }

        /// <summary>
        /// Alternative entry point for Run command.
        /// </summary>
        public static void Run(UIApplication uiapp)
        {
            ShowTool(uiapp);
        }
    }
}
