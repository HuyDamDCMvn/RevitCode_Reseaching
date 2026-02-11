# WPF/MVVM Examples for Revit

## Example 1: Selection-Aware ViewModel

ViewModel that responds to Revit selection changes via event subscription.

```csharp
public class SelectionAwareViewModel : ViewModelBase, IDisposable
{
    private readonly ExternalEvent _refreshEvent;
    private readonly SelectionRefreshHandler _refreshHandler;

    private ObservableCollection<ElementInfo> _selectedElements = new();
    public ObservableCollection<ElementInfo> SelectedElements
    {
        get => _selectedElements;
        set => SetProperty(ref _selectedElements, value);
    }

    public SelectionAwareViewModel(ExternalEvent refreshEvent, SelectionRefreshHandler handler)
    {
        _refreshEvent = refreshEvent;
        _refreshHandler = handler;
        _refreshHandler.SelectionRefreshed += OnSelectionRefreshed;
    }

    public void RequestSelectionRefresh()
    {
        _refreshHandler.SetCallback(OnSelectionData);
        _refreshEvent.Raise();
    }

    private void OnSelectionData(List<ElementInfo> elements)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            SelectedElements.Clear();
            foreach (var elem in elements)
                SelectedElements.Add(elem);
        });
    }

    public void Dispose()
    {
        _refreshHandler.SelectionRefreshed -= OnSelectionRefreshed;
    }
}

// DTO for element info (no Revit types)
public class ElementInfo
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string FamilyName { get; set; }
}
```

## Example 2: DataGrid with Element List

XAML for displaying elements in a grid with sorting/filtering.

```xml
<DataGrid ItemsSource="{Binding SelectedElements}"
          AutoGenerateColumns="False"
          IsReadOnly="True"
          CanUserSortColumns="True"
          SelectionMode="Extended"
          mah:DataGridHelper.EnableCellEditAssist="True">
    <DataGrid.Columns>
        <DataGridTextColumn Header="ID" Binding="{Binding Id}" Width="80"/>
        <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="*"/>
        <DataGridTextColumn Header="Category" Binding="{Binding Category}" Width="120"/>
        <DataGridTextColumn Header="Family" Binding="{Binding FamilyName}" Width="150"/>
    </DataGrid.Columns>
</DataGrid>
```

## Example 3: Async-Style Command Pattern

For operations that need to show progress and allow cancellation.

```csharp
public class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool> _canExecute;
    private CancellationTokenSource _cts;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool> canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object parameter)
    {
        if (_isExecuting) return;

        _isExecuting = true;
        _cts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();

        try
        {
            await _execute(_cts.Token);
        }
        finally
        {
            _isExecuting = false;
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void Cancel() => _cts?.Cancel();
}
```

## Example 4: Multi-Step Handler with Progress

Handler that reports progress back to ViewModel.

```csharp
public class BatchProcessHandler : IExternalEventHandler
{
    public BatchProcessRequest CurrentRequest { get; set; }

    public void Execute(UIApplication app)
    {
        var request = CurrentRequest;
        if (request == null) return;

        var doc = app.ActiveUIDocument?.Document;
        if (doc == null)
        {
            request.OnComplete?.Invoke(new BatchResult { Message = "No document" });
            return;
        }

        var result = new BatchResult();
        int total = request.ElementIds.Count;
        int processed = 0;

        using (var txGroup = new TransactionGroup(doc, "Batch Process"))
        {
            txGroup.Start();

            foreach (var id in request.ElementIds)
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    txGroup.RollBack();
                    result.Message = "Cancelled";
                    request.OnComplete?.Invoke(result);
                    return;
                }

                using (var tx = new Transaction(doc, "Process Element"))
                {
                    tx.Start();
                    
                    // Process single element
                    var elem = doc.GetElement(new ElementId(id));
                    if (elem != null)
                    {
                        // ... processing logic
                        processed++;
                    }
                    
                    tx.Commit();
                }

                // Report progress
                request.OnProgress?.Invoke(processed, total);
            }

            txGroup.Assimilate();
        }

        result.Success = true;
        result.ProcessedCount = processed;
        result.Message = $"Completed {processed}/{total}";
        request.OnComplete?.Invoke(result);
    }

    public string GetName() => "Batch Process Handler";
}

public class BatchProcessRequest
{
    public List<int> ElementIds { get; set; }
    public CancellationToken CancellationToken { get; set; }
    public Action<int, int> OnProgress { get; set; }  // (current, total)
    public Action<BatchResult> OnComplete { get; set; }
}
```

ViewModel usage:

```csharp
private async Task ExecuteBatchAsync(CancellationToken ct)
{
    var request = new BatchProcessRequest
    {
        ElementIds = SelectedElementIds.ToList(),
        CancellationToken = ct,
        OnProgress = (current, total) =>
        {
            Dispatcher.Invoke(() =>
            {
                Progress = (double)current / total * 100;
                StatusMessage = $"Processing {current}/{total}...";
            });
        },
        OnComplete = result =>
        {
            Dispatcher.Invoke(() =>
            {
                Progress = 0;
                StatusMessage = result.Message;
            });
        }
    };

    _handler.CurrentRequest = request;
    _externalEvent.Raise();
}
```

## Example 5: ComboBox with Category Selection

```xml
<ComboBox ItemsSource="{Binding AvailableCategories}"
          SelectedItem="{Binding SelectedCategory}"
          DisplayMemberPath="Name"
          mah:TextBoxHelper.Watermark="Select category..."
          mah:TextBoxHelper.UseFloatingWatermark="True"/>
```

ViewModel:

```csharp
// CategoryInfo is a DTO, not Revit's Category
public ObservableCollection<CategoryInfo> AvailableCategories { get; } = new();

private CategoryInfo _selectedCategory;
public CategoryInfo SelectedCategory
{
    get => _selectedCategory;
    set
    {
        if (SetProperty(ref _selectedCategory, value))
            OnCategoryChanged();
    }
}
```

## Example 6: Flyout Settings Panel (MahApps)

```xml
<mah:MetroWindow.Flyouts>
    <mah:FlyoutsControl>
        <mah:Flyout Header="Settings" 
                    Position="Right"
                    IsOpen="{Binding IsSettingsOpen}"
                    Width="300">
            <StackPanel Margin="16">
                <mah:ToggleSwitch Header="Auto-refresh selection"
                                  IsOn="{Binding AutoRefresh}"/>
                <mah:ToggleSwitch Header="Show element IDs"
                                  IsOn="{Binding ShowIds}"/>
                <Separator Margin="0,16"/>
                <Button Content="Reset to Defaults"
                        Command="{Binding ResetSettingsCommand}"/>
            </StackPanel>
        </mah:Flyout>
    </mah:FlyoutsControl>
</mah:MetroWindow.Flyouts>
```

## Example 7: Icon Buttons (MahApps.Metro.IconPacks)

```xml
xmlns:iconPacks="http://metro.mahapps.com/winfx/xaml/iconpacks"

<Button Command="{Binding RefreshCommand}" 
        ToolTip="Refresh selection"
        Style="{DynamicResource MahApps.Styles.Button.Circle}">
    <iconPacks:PackIconMaterial Kind="Refresh" Width="16" Height="16"/>
</Button>

<Button Command="{Binding ExportCommand}"
        ToolTip="Export to Excel">
    <StackPanel Orientation="Horizontal">
        <iconPacks:PackIconMaterial Kind="FileExcel" Width="16" Height="16" Margin="0,0,8,0"/>
        <TextBlock Text="Export"/>
    </StackPanel>
</Button>
```

## Example 8: Validation with Error Template

```xml
<Style TargetType="TextBox" BasedOn="{StaticResource MahApps.Styles.TextBox}">
    <Setter Property="Validation.ErrorTemplate">
        <Setter.Value>
            <ControlTemplate>
                <StackPanel>
                    <AdornedElementPlaceholder x:Name="placeholder"/>
                    <TextBlock Text="{Binding ElementName=placeholder, Path=AdornedElement.(Validation.Errors)[0].ErrorContent}"
                               Foreground="{DynamicResource MahApps.Brushes.Validation5}"
                               FontSize="11" Margin="4,2,0,0"/>
                </StackPanel>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
    <Style.Triggers>
        <Trigger Property="Validation.HasError" Value="True">
            <Setter Property="BorderBrush" Value="{DynamicResource MahApps.Brushes.Validation5}"/>
        </Trigger>
    </Style.Triggers>
</Style>
```

## Example 9: Keyboard Shortcuts

```csharp
// In code-behind or via InputBindings
public MainWindow(MainWindowViewModel viewModel)
{
    InitializeComponent();
    DataContext = viewModel;

    // Keyboard shortcuts
    InputBindings.Add(new KeyBinding(viewModel.ApplyCommand, Key.Enter, ModifierKeys.Control));
    InputBindings.Add(new KeyBinding(viewModel.RefreshCommand, Key.F5, ModifierKeys.None));
    InputBindings.Add(new KeyBinding(viewModel.CancelCommand, Key.Escape, ModifierKeys.None)
    {
        CommandParameter = this
    });
}
```

Or in XAML:

```xml
<Window.InputBindings>
    <KeyBinding Key="Enter" Modifiers="Control" Command="{Binding ApplyCommand}"/>
    <KeyBinding Key="F5" Command="{Binding RefreshCommand}"/>
    <KeyBinding Key="Escape" Command="{Binding CancelCommand}" 
                CommandParameter="{Binding RelativeSource={RelativeSource AncestorType=Window}}"/>
</Window.InputBindings>
```

## Example 10: Dialog Result Pattern

For modal dialogs that need to return a result.

```csharp
public class DialogViewModel : ViewModelBase
{
    public bool? DialogResult { get; private set; }
    public string ResultValue { get; private set; }

    public ICommand OkCommand { get; }
    public ICommand CancelCommand { get; }

    public DialogViewModel()
    {
        OkCommand = new RelayCommand(ExecuteOk, CanExecuteOk);
        CancelCommand = new RelayCommand(ExecuteCancel);
    }

    private void ExecuteOk(object parameter)
    {
        ResultValue = SomeProperty;
        DialogResult = true;
        CloseRequested?.Invoke(this, true);
    }

    private void ExecuteCancel(object parameter)
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, false);
    }

    public event EventHandler<bool> CloseRequested;
}
```

Code-behind:

```csharp
public partial class DialogWindow : MetroWindow
{
    public DialogWindow(DialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (s, result) =>
        {
            DialogResult = result;
            Close();
        };
    }
}
```
