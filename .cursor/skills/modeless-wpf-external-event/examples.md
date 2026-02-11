# Modeless WPF + ExternalEvent - Complete Examples

## Full Working Example: Parameter Editor

A complete implementation of a modeless WPF tool that edits element parameters.

### Project Structure

```
YourAddin/
├── Commands/
│   └── ShowParameterEditorCommand.cs
├── Handlers/
│   └── ParameterEditorHandler.cs
├── ViewModels/
│   ├── ParameterEditorViewModel.cs
│   └── ViewModelBase.cs
├── Views/
│   └── ParameterEditorWindow.xaml
├── Requests/
│   ├── RequestType.cs
│   └── EditorRequest.cs
├── Utilities/
│   └── RelayCommand.cs
└── YourAddin.addin
```

---

## Request DTOs

### RequestType.cs

```csharp
namespace YourAddin.Requests
{
    public enum RequestType
    {
        None,
        GetSelection,
        GetParameterValue,
        SetParameterValue
    }
}
```

### EditorRequest.cs

```csharp
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace YourAddin.Requests
{
    /// <summary>
    /// Immutable request DTO for passing data from ViewModel to Handler.
    /// </summary>
    public sealed class EditorRequest
    {
        public RequestType Type { get; }
        public IReadOnlyList<ElementId> ElementIds { get; }
        public string ParameterName { get; }
        public string NewValue { get; }

        private EditorRequest(RequestType type)
        {
            Type = type;
            ElementIds = new List<ElementId>();
        }

        // Factory methods ensure valid request construction
        
        public static EditorRequest GetSelection()
        {
            return new EditorRequest(RequestType.GetSelection);
        }

        public static EditorRequest GetParameterValue(
            IEnumerable<ElementId> ids, 
            string parameterName)
        {
            return new EditorRequest(RequestType.GetParameterValue)
            {
                ElementIds = ids.ToList(),
                ParameterName = parameterName
            };
        }

        public static EditorRequest SetParameterValue(
            IEnumerable<ElementId> ids,
            string parameterName,
            string newValue)
        {
            return new EditorRequest(RequestType.SetParameterValue)
            {
                ElementIds = ids.ToList(),
                ParameterName = parameterName,
                NewValue = newValue
            };
        }

        // Private setters for immutability with object initializer
        private IReadOnlyList<ElementId> ElementIds { init; get; }
        private string ParameterName { init; get; }
        private string NewValue { init; get; }
    }
}
```

---

## Handler

### ParameterEditorHandler.cs

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using YourAddin.Requests;

namespace YourAddin.Handlers
{
    /// <summary>
    /// Handles all Revit API calls for the Parameter Editor.
    /// This is the ONLY place where Revit API should be called.
    /// </summary>
    public class ParameterEditorHandler : IExternalEventHandler
    {
        private EditorRequest _currentRequest;
        private readonly object _lock = new object();

        // Events to communicate results back to ViewModel
        public event Action<List<ElementId>> OnSelectionReceived;
        public event Action<string> OnParameterValueReceived;
        public event Action<int, int> OnParameterSetComplete; // (success, total)
        public event Action<string> OnError;

        public void SetRequest(EditorRequest request)
        {
            lock (_lock)
            {
                _currentRequest = request;
            }
        }

        public void Execute(UIApplication app)
        {
            EditorRequest request;
            lock (_lock)
            {
                request = _currentRequest;
                _currentRequest = null;
            }

            if (request == null) return;

            try
            {
                switch (request.Type)
                {
                    case RequestType.GetSelection:
                        ExecuteGetSelection(app);
                        break;

                    case RequestType.GetParameterValue:
                        ExecuteGetParameterValue(app, request);
                        break;

                    case RequestType.SetParameterValue:
                        ExecuteSetParameterValue(app, request);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
        }

        private void ExecuteGetSelection(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var ids = uidoc.Selection.GetElementIds().ToList();
            OnSelectionReceived?.Invoke(ids);
        }

        private void ExecuteGetParameterValue(UIApplication app, EditorRequest request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            if (!request.ElementIds.Any())
            {
                OnParameterValueReceived?.Invoke(string.Empty);
                return;
            }

            // Get value from first element
            var firstId = request.ElementIds.First();
            var elem = doc.GetElement(firstId);
            var param = elem?.LookupParameter(request.ParameterName);

            string value = string.Empty;
            if (param != null && param.HasValue)
            {
                value = GetParameterValueAsString(param);
            }

            OnParameterValueReceived?.Invoke(value);
        }

        private void ExecuteSetParameterValue(UIApplication app, EditorRequest request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            int successCount = 0;
            int totalCount = request.ElementIds.Count;

            using (var tx = new Transaction(doc, "Set Parameter Value"))
            {
                tx.Start();

                foreach (var id in request.ElementIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;

                    var param = elem.LookupParameter(request.ParameterName);
                    if (param == null || param.IsReadOnly) continue;

                    if (SetParameterValue(param, request.NewValue))
                    {
                        successCount++;
                    }
                }

                tx.Commit();
            }

            OnParameterSetComplete?.Invoke(successCount, totalCount);
        }

        private string GetParameterValueAsString(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    return param.AsDouble().ToString("F4");
                case StorageType.ElementId:
                    return param.AsElementId().IntegerValue.ToString();
                default:
                    return string.Empty;
            }
        }

        private bool SetParameterValue(Parameter param, string value)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        param.Set(value);
                        return true;
                    case StorageType.Integer:
                        if (int.TryParse(value, out int intVal))
                        {
                            param.Set(intVal);
                            return true;
                        }
                        break;
                    case StorageType.Double:
                        if (double.TryParse(value, out double dblVal))
                        {
                            param.Set(dblVal);
                            return true;
                        }
                        break;
                }
            }
            catch
            {
                // Parameter set failed
            }
            return false;
        }

        public string GetName() => "ParameterEditorHandler";
    }
}
```

---

## ViewModel

### ViewModelBase.cs

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YourAddin.ViewModels
{
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
```

### ParameterEditorViewModel.cs

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using YourAddin.Handlers;
using YourAddin.Requests;
using YourAddin.Utilities;

namespace YourAddin.ViewModels
{
    public class ParameterEditorViewModel : ViewModelBase
    {
        private readonly ExternalEvent _externalEvent;
        private readonly ParameterEditorHandler _handler;
        private readonly Action _closeWindow;

        private List<ElementId> _selectedIds = new List<ElementId>();
        private string _parameterName = string.Empty;
        private string _parameterValue = string.Empty;
        private string _statusMessage = "Select elements in Revit and click 'Get Selection'";
        private bool _isBusy;

        public ParameterEditorViewModel(
            ExternalEvent externalEvent,
            ParameterEditorHandler handler,
            Action closeWindow)
        {
            _externalEvent = externalEvent;
            _handler = handler;
            _closeWindow = closeWindow;

            // Subscribe to handler events
            _handler.OnSelectionReceived += HandleSelectionReceived;
            _handler.OnParameterValueReceived += HandleParameterValueReceived;
            _handler.OnParameterSetComplete += HandleParameterSetComplete;
            _handler.OnError += HandleError;

            // Initialize commands
            GetSelectionCommand = new RelayCommand(ExecuteGetSelection, _ => !IsBusy);
            GetValueCommand = new RelayCommand(ExecuteGetValue, CanExecuteGetValue);
            SetValueCommand = new RelayCommand(ExecuteSetValue, CanExecuteSetValue);
            CloseCommand = new RelayCommand(_ => _closeWindow?.Invoke());
        }

        #region Properties

        public string ParameterName
        {
            get => _parameterName;
            set
            {
                if (SetProperty(ref _parameterName, value))
                {
                    GetValueCommand.RaiseCanExecuteChanged();
                    SetValueCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string ParameterValue
        {
            get => _parameterValue;
            set => SetProperty(ref _parameterValue, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    GetSelectionCommand.RaiseCanExecuteChanged();
                    GetValueCommand.RaiseCanExecuteChanged();
                    SetValueCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public int SelectionCount => _selectedIds.Count;

        #endregion

        #region Commands

        public RelayCommand GetSelectionCommand { get; }
        public RelayCommand GetValueCommand { get; }
        public RelayCommand SetValueCommand { get; }
        public RelayCommand CloseCommand { get; }

        #endregion

        #region Command Execution

        private void ExecuteGetSelection(object _)
        {
            IsBusy = true;
            StatusMessage = "Getting selection...";
            
            _handler.SetRequest(EditorRequest.GetSelection());
            _externalEvent.Raise();
        }

        private void ExecuteGetValue(object _)
        {
            if (!ValidateSelection()) return;

            IsBusy = true;
            StatusMessage = "Reading parameter value...";
            
            _handler.SetRequest(EditorRequest.GetParameterValue(_selectedIds, ParameterName));
            _externalEvent.Raise();
        }

        private bool CanExecuteGetValue(object _)
        {
            return !IsBusy 
                && _selectedIds.Any() 
                && !string.IsNullOrWhiteSpace(ParameterName);
        }

        private void ExecuteSetValue(object _)
        {
            if (!ValidateSelection()) return;

            IsBusy = true;
            StatusMessage = $"Setting parameter on {_selectedIds.Count} elements...";
            
            _handler.SetRequest(EditorRequest.SetParameterValue(
                _selectedIds, 
                ParameterName, 
                ParameterValue));
            _externalEvent.Raise();
        }

        private bool CanExecuteSetValue(object _)
        {
            return !IsBusy 
                && _selectedIds.Any() 
                && !string.IsNullOrWhiteSpace(ParameterName);
        }

        private bool ValidateSelection()
        {
            if (!_selectedIds.Any())
            {
                StatusMessage = "No elements selected. Click 'Get Selection' first.";
                return false;
            }
            return true;
        }

        #endregion

        #region Handler Callbacks

        private void HandleSelectionReceived(List<ElementId> ids)
        {
            _selectedIds = ids;
            IsBusy = false;
            OnPropertyChanged(nameof(SelectionCount));
            
            StatusMessage = ids.Any() 
                ? $"Selected {ids.Count} element(s)" 
                : "No elements selected";
            
            GetValueCommand.RaiseCanExecuteChanged();
            SetValueCommand.RaiseCanExecuteChanged();
        }

        private void HandleParameterValueReceived(string value)
        {
            IsBusy = false;
            ParameterValue = value;
            StatusMessage = string.IsNullOrEmpty(value) 
                ? "Parameter not found or has no value" 
                : "Parameter value loaded";
        }

        private void HandleParameterSetComplete(int success, int total)
        {
            IsBusy = false;
            StatusMessage = $"Updated {success} of {total} element(s)";
        }

        private void HandleError(string message)
        {
            IsBusy = false;
            StatusMessage = $"Error: {message}";
        }

        #endregion

        public void Cleanup()
        {
            _handler.OnSelectionReceived -= HandleSelectionReceived;
            _handler.OnParameterValueReceived -= HandleParameterValueReceived;
            _handler.OnParameterSetComplete -= HandleParameterSetComplete;
            _handler.OnError -= HandleError;
        }
    }
}
```

---

## View

### ParameterEditorWindow.xaml

```xml
<Window x:Class="YourAddin.Views.ParameterEditorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Parameter Editor" 
        Height="350" Width="400"
        WindowStartupLocation="CenterScreen"
        Topmost="True"
        ResizeMode="CanResizeWithGrip">
    
    <Window.Resources>
        <Style TargetType="Button">
            <Setter Property="Padding" Value="12,6"/>
            <Setter Property="Margin" Value="0,0,8,0"/>
        </Style>
        <Style TargetType="TextBox">
            <Setter Property="Padding" Value="4"/>
            <Setter Property="Margin" Value="0,4,0,0"/>
        </Style>
    </Window.Resources>
    
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <!-- Selection Section -->
        <GroupBox Grid.Row="0" Header="Selection" Margin="0,0,0,12">
            <StackPanel Margin="8">
                <StackPanel Orientation="Horizontal">
                    <Button Content="Get Selection" 
                            Command="{Binding GetSelectionCommand}"/>
                    <TextBlock VerticalAlignment="Center">
                        <Run Text="Selected: "/>
                        <Run Text="{Binding SelectionCount, Mode=OneWay}"/>
                        <Run Text=" element(s)"/>
                    </TextBlock>
                </StackPanel>
            </StackPanel>
        </GroupBox>
        
        <!-- Parameter Section -->
        <GroupBox Grid.Row="1" Header="Parameter" Margin="0,0,0,12">
            <StackPanel Margin="8">
                <TextBlock Text="Parameter Name:"/>
                <TextBox Text="{Binding ParameterName, UpdateSourceTrigger=PropertyChanged}"
                         ToolTip="Enter the exact parameter name"/>
                
                <TextBlock Text="Value:" Margin="0,8,0,0"/>
                <TextBox Text="{Binding ParameterValue, UpdateSourceTrigger=PropertyChanged}"
                         AcceptsReturn="False"/>
            </StackPanel>
        </GroupBox>
        
        <!-- Actions -->
        <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,0,0,12">
            <Button Content="Get Value" 
                    Command="{Binding GetValueCommand}"
                    ToolTip="Read parameter value from selected elements"/>
            <Button Content="Set Value" 
                    Command="{Binding SetValueCommand}"
                    ToolTip="Apply value to all selected elements"/>
        </StackPanel>
        
        <!-- Status -->
        <Border Grid.Row="4" 
                Background="#F5F5F5" 
                CornerRadius="4" 
                Padding="8">
            <TextBlock Text="{Binding StatusMessage}" 
                       TextWrapping="Wrap"
                       Foreground="#666"/>
        </Border>
        
        <!-- Close Button -->
        <Button Grid.Row="5" 
                Content="Close" 
                Command="{Binding CloseCommand}"
                HorizontalAlignment="Right"
                Margin="0,12,0,0"/>
    </Grid>
</Window>
```

### ParameterEditorWindow.xaml.cs

```csharp
using System.Windows;
using YourAddin.ViewModels;

namespace YourAddin.Views
{
    public partial class ParameterEditorWindow : Window
    {
        public ParameterEditorWindow()
        {
            InitializeComponent();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            
            // Cleanup ViewModel subscriptions
            if (DataContext is ParameterEditorViewModel vm)
            {
                vm.Cleanup();
            }
        }
    }
}
```

---

## Command

### ShowParameterEditorCommand.cs

```csharp
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using YourAddin.Handlers;
using YourAddin.ViewModels;
using YourAddin.Views;

namespace YourAddin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShowParameterEditorCommand : IExternalCommand
    {
        // Static fields keep references alive
        private static ParameterEditorWindow _window;
        private static ExternalEvent _externalEvent;
        private static ParameterEditorHandler _handler;

        public Result Execute(
            ExternalCommandData commandData, 
            ref string message, 
            ElementSet elements)
        {
            try
            {
                // If window already exists and is open, activate it
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return Result.Succeeded;
                }

                // Create handler and external event
                _handler = new ParameterEditorHandler();
                _externalEvent = ExternalEvent.Create(_handler);

                // Create window and ViewModel
                _window = new ParameterEditorWindow();
                var viewModel = new ParameterEditorViewModel(
                    _externalEvent,
                    _handler,
                    () => _window.Close());

                _window.DataContext = viewModel;

                // Cleanup on window close
                _window.Closed += OnWindowClosed;

                // Show modeless
                _window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void OnWindowClosed(object sender, EventArgs e)
        {
            // Dispose external event
            _externalEvent?.Dispose();
            
            // Clear references
            _externalEvent = null;
            _handler = null;
            _window = null;
        }
    }
}
```

---

## RelayCommand

### RelayCommand.cs {#relaycommand}

```csharp
using System;
using System.Windows.Input;

namespace YourAddin.Utilities
{
    /// <summary>
    /// A simple implementation of ICommand for MVVM.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
```

---

## Add-in Manifest

### YourAddin.addin

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Name>Parameter Editor</Name>
    <FullClassName>YourAddin.Commands.ShowParameterEditorCommand</FullClassName>
    <Assembly>YourAddin.dll</Assembly>
    <AddInId>GENERATE-NEW-GUID-HERE</AddInId>
    <VendorId>YourCompany</VendorId>
    <VendorDescription>Your Company Name</VendorDescription>
  </AddIn>
</RevitAddIns>
```

---

## Usage Notes

1. **Build and deploy** the DLL and `.addin` file to Revit's AddIns folder
2. **Launch Revit** and run the command from Add-Ins tab
3. **Select elements** in Revit, then click "Get Selection" in the dialog
4. **Enter parameter name** exactly as it appears in Revit
5. **Get Value** to read current value, **Set Value** to apply changes

## Testing Checklist

- [ ] Window stays open while working in Revit
- [ ] Selection updates correctly
- [ ] Parameter values read correctly for different storage types
- [ ] Parameter values set correctly
- [ ] Error messages display properly
- [ ] Window reactivates if command run while already open
- [ ] Resources cleaned up when window closes
