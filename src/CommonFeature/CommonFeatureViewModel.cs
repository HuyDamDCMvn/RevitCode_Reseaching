using System;
using System.Windows;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CommonFeature
{
    /// <summary>
    /// ViewModel for CommonFeature window.
    /// Never calls Revit API directly - uses ExternalEvent pattern.
    /// Uses CommunityToolkit.Mvvm for MVVM infrastructure.
    /// </summary>
    public partial class CommonFeatureViewModel : ViewModelBase
    {
        private readonly ExternalEvent _externalEvent;
        private readonly CommonFeatureHandler _handler;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready to use";

        // Track active state for each feature (for UI styling)
        // These stay true while the feature window is open
        [ObservableProperty]
        private bool _isIsolateActive;

        [ObservableProperty]
        private bool _isInfoActive;

        [ObservableProperty]
        private bool _isParameterActive;

        [ObservableProperty]
        private bool _isBoundaryActive;

        public CommonFeatureViewModel(ExternalEvent externalEvent, CommonFeatureHandler handler)
        {
            _externalEvent = externalEvent;
            _handler = handler;

            // Set ExternalEvent reference in handler for async operations (e.g., parameter update)
            _handler.SetExternalEvent(externalEvent);

            // Subscribe to handler callbacks
            _handler.OnOperationCompleted += HandleOperationCompleted;
            _handler.OnError += HandleError;
            _handler.OnFeatureWindowClosed += HandleFeatureWindowClosed;
        }

        #region Commands

        // All commands can execute anytime - features run in parallel
        [RelayCommand]
        private void Isolate()
        {
            IsIsolateActive = true;
            StatusMessage = "Opening Isolate...";
            _handler.SetRequest(CommonFeatureRequest.Isolate());
            _externalEvent.Raise();
        }

        [RelayCommand]
        private void GetInformation()
        {
            IsInfoActive = true;
            StatusMessage = "Opening Get Information...";
            _handler.SetRequest(CommonFeatureRequest.GetInformation());
            _externalEvent.Raise();
        }

        [RelayCommand]
        private void ShowParameter()
        {
            IsParameterActive = true;
            StatusMessage = "Opening Parameter Manager...";
            _handler.SetRequest(CommonFeatureRequest.ShowParameter());
            _externalEvent.Raise();
        }

        [RelayCommand]
        private void ShowBoundary()
        {
            IsBoundaryActive = true;
            StatusMessage = "Opening Section Box...";
            _handler.SetRequest(CommonFeatureRequest.ShowBoundary());
            _externalEvent.Raise();
        }

        #endregion

        #region Callbacks

        private void HandleOperationCompleted(string message)
        {
            // Update UI on UI thread
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusMessage = message;
            }));
        }

        private void HandleError(string message)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusMessage = $"Error: {message}";
            }));
        }

        private void HandleFeatureWindowClosed(string featureName)
        {
            // Reset active state when feature window is closed
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (featureName)
                {
                    case "Isolate": IsIsolateActive = false; break;
                    case "Info": IsInfoActive = false; break;
                    case "Parameter": IsParameterActive = false; break;
                    case "Boundary": IsBoundaryActive = false; break;
                }
            }));
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            _handler.OnOperationCompleted -= HandleOperationCompleted;
            _handler.OnError -= HandleError;
            _handler.OnFeatureWindowClosed -= HandleFeatureWindowClosed;
        }

        #endregion
    }
}
