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
        [NotifyCanExecuteChangedFor(nameof(IsolateCommand))]
        [NotifyCanExecuteChangedFor(nameof(GetInformationCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShowParameterCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShowBoundaryCommand))]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready to use";

        public CommonFeatureViewModel(ExternalEvent externalEvent, CommonFeatureHandler handler)
        {
            _externalEvent = externalEvent;
            _handler = handler;

            // Set ExternalEvent reference in handler for async operations (e.g., parameter update)
            _handler.SetExternalEvent(externalEvent);

            // Subscribe to handler callbacks
            _handler.OnOperationCompleted += HandleOperationCompleted;
            _handler.OnError += HandleError;
        }

        #region Commands

        private bool CanExecuteCommand() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanExecuteCommand))]
        private void Isolate()
        {
            IsBusy = true;
            StatusMessage = "Isolating elements...";
            _handler.SetRequest(CommonFeatureRequest.Isolate());
            _externalEvent.Raise();
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommand))]
        private void GetInformation()
        {
            IsBusy = true;
            StatusMessage = "Getting information...";
            _handler.SetRequest(CommonFeatureRequest.GetInformation());
            _externalEvent.Raise();
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommand))]
        private void ShowParameter()
        {
            IsBusy = true;
            StatusMessage = "Loading parameters...";
            _handler.SetRequest(CommonFeatureRequest.ShowParameter());
            _externalEvent.Raise();
        }

        [RelayCommand(CanExecute = nameof(CanExecuteCommand))]
        private void ShowBoundary()
        {
            IsBusy = true;
            StatusMessage = "Calculating boundary...";
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
                IsBusy = false;
                StatusMessage = message;
            }));
        }

        private void HandleError(string message)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsBusy = false;
                StatusMessage = $"Error: {message}";
            }));
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            _handler.OnOperationCompleted -= HandleOperationCompleted;
            _handler.OnError -= HandleError;
        }

        #endregion
    }
}
