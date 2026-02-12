using System;
using System.Windows;
using System.Windows.Interop;

namespace HD.Core.Helpers
{
    /// <summary>
    /// Helper for WPF windows in Revit context.
    /// </summary>
    public static class WpfHelper
    {
        /// <summary>
        /// Set Revit main window as owner (keeps window on top of Revit).
        /// </summary>
        public static void SetRevitAsOwner(Window window, IntPtr revitMainWindowHandle)
        {
            if (window == null || revitMainWindowHandle == IntPtr.Zero) return;
            var helper = new WindowInteropHelper(window);
            helper.Owner = revitMainWindowHandle;
        }

        /// <summary>
        /// Execute action on UI thread.
        /// </summary>
        public static void InvokeOnUI(Action action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                action?.Invoke();
                return;
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                action?.Invoke();
            }
            else
            {
                Application.Current.Dispatcher.BeginInvoke(action);
            }
        }

        /// <summary>
        /// Execute action on UI thread and wait for completion.
        /// </summary>
        public static void InvokeOnUISync(Action action)
        {
            if (Application.Current?.Dispatcher == null)
            {
                action?.Invoke();
                return;
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                action?.Invoke();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(action);
            }
        }

        /// <summary>
        /// Execute function on UI thread and return result.
        /// </summary>
        public static T InvokeOnUI<T>(Func<T> func)
        {
            if (Application.Current?.Dispatcher == null)
                return func != null ? func() : default;

            if (Application.Current.Dispatcher.CheckAccess())
                return func != null ? func() : default;

            return Application.Current.Dispatcher.Invoke(func);
        }
    }
}
