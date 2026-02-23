using System;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace CommonFeature
{
    /// <summary>
    /// Entry point for pyRevit launcher.
    /// Manages single-instance modeless window.
    /// </summary>
    public static class Entry
    {
        private static CommonFeatureWindow _window;
        private static ExternalEvent _externalEvent;
        private static CommonFeatureHandler _handler;

        /// <summary>
        /// Show or focus the CommonFeature window.
        /// Called by pyRevit script.
        /// </summary>
        public static void ShowTool(UIApplication uiapp)
        {
            try
            {
                if (uiapp == null)
                {
                    TaskDialog.Show("CommonFeature Error", "UIApplication is null.");
                    return;
                }

                if (uiapp.ActiveUIDocument?.Document == null)
                {
                    TaskDialog.Show("CommonFeature Error", "No active document. Please open a project first.");
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
                    _handler = new CommonFeatureHandler();
                }
                
                if (_externalEvent == null)
                {
                    _externalEvent = ExternalEvent.Create(_handler);
                }

                // Create ViewModel and Window
                var viewModel = new CommonFeatureViewModel(_externalEvent, _handler);
                _window = new CommonFeatureWindow
                {
                    DataContext = viewModel
                };

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
            }
            catch (Exception ex)
            {
                TaskDialog.Show("CommonFeature Error", 
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
