# WPF/MVVM Architect Examples

## Example 1: Complete Tool Structure

Demonstrates full architecture with all layers properly separated.

### Request DTO

```csharp
namespace MyTool.Requests
{
    public enum RequestType
    {
        None,
        GetSelection,
        ApplyChanges,
        Export
    }

    public sealed class ToolRequest
    {
        public RequestType Type { get; }
        public IReadOnlyList<int> ElementIds { get; init; }
        public Dictionary<string, object> Payload { get; init; }
        public CancellationToken CancellationToken { get; init; }
        public Action<int, int> OnProgress { get; init; }
        public Action<ToolResult> OnComplete { get; init; }

        private ToolRequest(RequestType type) => Type = type;

        public static ToolRequest GetSelection() => new(RequestType.GetSelection);
        
        public static ToolRequest ApplyChanges(IEnumerable<int> ids, Dictionary<string, object> changes)
            => new(RequestType.ApplyChanges) { ElementIds = ids.ToList(), Payload = changes };
            
        public static ToolRequest Export(IEnumerable<int> ids, string outputPath)
            => new(RequestType.Export) { ElementIds = ids.ToList(), Payload = new() { ["Path"] = outputPath } };
    }

    public class ToolResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<ElementInfo> Elements { get; set; }
        public int ProcessedCount { get; set; }
    }
}
```

### Handler (Revit API Boundary)

```csharp
namespace MyTool.Handlers
{
    public class ToolHandler : IExternalEventHandler
    {
        private ToolRequest _request;
        private readonly object _lock = new();

        public event Action<List<ElementInfo>> OnSelectionReceived;
        public event Action<ToolResult> OnOperationComplete;
        public event Action<string> OnError;

        public void SetRequest(ToolRequest request)
        {
            lock (_lock) { _request = request; }
        }

        public void Execute(UIApplication app)
        {
            ToolRequest request;
            lock (_lock) { request = _request; _request = null; }
            if (request == null) return;

            try
            {
                switch (request.Type)
                {
                    case RequestType.GetSelection:
                        ExecuteGetSelection(app);
                        break;
                    case RequestType.ApplyChanges:
                        ExecuteApplyChanges(app, request);
                        break;
                    case RequestType.Export:
                        ExecuteExport(app, request);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"[{request.Type}] {ex.Message}");
            }
        }

        private void ExecuteGetSelection(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnSelectionReceived?.Invoke(new List<ElementInfo>());
                return;
            }

            var doc = uidoc.Document;
            var elements = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Select(MapToDto)
                .ToList();

            OnSelectionReceived?.Invoke(elements);
        }

        private void ExecuteApplyChanges(UIApplication app, ToolRequest request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                request.OnComplete?.Invoke(new ToolResult { Message = "No document open" });
                return;
            }

            int total = request.ElementIds.Count;
            int processed = 0;

            using var tx = new Transaction(doc, "Apply Changes");
            tx.Start();

            foreach (var id in request.ElementIds)
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    tx.RollBack();
                    request.OnComplete?.Invoke(new ToolResult { Message = "Cancelled" });
                    return;
                }

                var element = doc.GetElement(new ElementId(id));
                if (element != null)
                {
                    ApplyChangesToElement(element, request.Payload);
                    processed++;
                }

                request.OnProgress?.Invoke(processed, total);
            }

            tx.Commit();
            request.OnComplete?.Invoke(new ToolResult 
            { 
                Success = true, 
                ProcessedCount = processed,
                Message = $"Updated {processed} elements"
            });
        }

        private void ExecuteExport(UIApplication app, ToolRequest request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                request.OnComplete?.Invoke(new ToolResult { Message = "No document open" });
                return;
            }

            var elements = request.ElementIds
                .Select(id => doc.GetElement(new ElementId(id)))
                .Where(e => e != null)
                .Select(MapToDto)
                .ToList();

            // Export is data gathering only - actual file write happens in ViewModel
            request.OnComplete?.Invoke(new ToolResult 
            { 
                Success = true, 
                Elements = elements,
                Message = $"Exported {elements.Count} elements"
            });
        }

        private ElementInfo MapToDto(Element e)
        {
            return new ElementInfo
            {
                Id = e.Id.IntegerValue,
                Name = e.Name,
                Category = e.Category?.Name ?? "Unknown",
                FamilyName = (e as FamilyInstance)?.Symbol?.Family?.Name,
                Parameters = GetParameterDict(e)
            };
        }

        private Dictionary<string, string> GetParameterDict(Element e)
        {
            return e.Parameters
                .Cast<Parameter>()
                .Where(p => p.HasValue && p.Definition != null)
                .ToDictionary(
                    p => p.Definition.Name,
                    p => p.AsValueString() ?? p.AsString() ?? ""
                );
        }

        private void ApplyChangesToElement(Element element, Dictionary<string, object> changes)
        {
            foreach (var kvp in changes)
            {
                var param = element.LookupParameter(kvp.Key);
                if (param != null && !param.IsReadOnly)
                {
                    // Set parameter value based on type
                    if (kvp.Value is string s) param.Set(s);
                    else if (kvp.Value is double d) param.Set(d);
                    else if (kvp.Value is int i) param.Set(i);
                }
            }
        }

        public string GetName() => "MyTool.ToolHandler";
    }
}
```

### ViewModel

```csharp
namespace MyTool.ViewModels
{
    public class ToolViewModel : ViewModelBase
    {
        private readonly ExternalEvent _externalEvent;
        private readonly ToolHandler _handler;
        private CancellationTokenSource _cts;

        // Collections (DTOs only)
        public ObservableCollection<ElementInfo> Elements { get; } = new();

        // State
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { SetProperty(ref _isBusy, value); RaiseAllCommandsCanExecute(); }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        private string _statusMessage = "Ready";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        // Commands
        public RelayCommand RefreshCommand { get; }
        public RelayCommand ApplyCommand { get; }
        public RelayCommand ExportCommand { get; }
        public RelayCommand CancelCommand { get; }

        public ToolViewModel(ExternalEvent externalEvent, ToolHandler handler)
        {
            _externalEvent = externalEvent;
            _handler = handler;

            // Subscribe to handler callbacks
            _handler.OnSelectionReceived += HandleSelectionReceived;
            _handler.OnOperationComplete += HandleOperationComplete;
            _handler.OnError += HandleError;

            // Initialize commands
            RefreshCommand = new RelayCommand(ExecuteRefresh, _ => !IsBusy);
            ApplyCommand = new RelayCommand(ExecuteApply, _ => !IsBusy && Elements.Any());
            ExportCommand = new RelayCommand(ExecuteExport, _ => !IsBusy && Elements.Any());
            CancelCommand = new RelayCommand(ExecuteCancel, _ => IsBusy);
        }

        private void ExecuteRefresh(object _)
        {
            IsBusy = true;
            StatusMessage = "Getting selection...";
            _handler.SetRequest(ToolRequest.GetSelection());
            _externalEvent.Raise();
        }

        private void ExecuteApply(object _)
        {
            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            StatusMessage = "Applying changes...";

            var changes = new Dictionary<string, object>
            {
                ["Comments"] = "Updated by tool"
            };

            var request = ToolRequest.ApplyChanges(
                Elements.Select(e => e.Id),
                changes
            );

            // Attach progress and completion handlers
            var requestWithCallbacks = new ToolRequest
            {
                Type = RequestType.ApplyChanges,
                ElementIds = request.ElementIds,
                Payload = request.Payload,
                CancellationToken = _cts.Token,
                OnProgress = (current, total) => Dispatcher.Invoke(() =>
                {
                    Progress = (double)current / total * 100;
                    StatusMessage = $"Processing {current}/{total}...";
                }),
                OnComplete = result => Dispatcher.Invoke(() =>
                {
                    IsBusy = false;
                    Progress = 0;
                    StatusMessage = result.Message;
                    CleanupCts();
                })
            };

            _handler.SetRequest(requestWithCallbacks);
            _externalEvent.Raise();
        }

        private void ExecuteExport(object _)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = "csv"
            };

            if (dialog.ShowDialog() != true) return;

            IsBusy = true;
            StatusMessage = "Exporting...";

            // Request Revit data, then write file in callback
            _handler.SetRequest(ToolRequest.Export(
                Elements.Select(e => e.Id),
                dialog.FileName
            ));
            _externalEvent.Raise();
        }

        private void ExecuteCancel(object _)
        {
            _cts?.Cancel();
            StatusMessage = "Cancelling...";
        }

        // Callbacks from Handler (may be called from Revit thread)
        private void HandleSelectionReceived(List<ElementInfo> elements)
        {
            Dispatcher.Invoke(() =>
            {
                Elements.Clear();
                foreach (var e in elements) Elements.Add(e);
                IsBusy = false;
                StatusMessage = $"Loaded {elements.Count} elements";
            });
        }

        private void HandleOperationComplete(ToolResult result)
        {
            Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                Progress = 0;
                StatusMessage = result.Message;

                // If export, write file now (outside Revit thread)
                if (result.Elements != null)
                {
                    WriteExportFile(result.Elements);
                }

                CleanupCts();
            });
        }

        private void HandleError(string message)
        {
            Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                Progress = 0;
                StatusMessage = $"Error: {message}";
                CleanupCts();
            });
        }

        private async void WriteExportFile(List<ElementInfo> elements)
        {
            // File I/O happens on background thread, NOT in Revit handler
            try
            {
                var path = _pendingExportPath;
                await Task.Run(() =>
                {
                    var lines = new List<string> { "Id,Name,Category,Family" };
                    lines.AddRange(elements.Select(e => 
                        $"{e.Id},{Escape(e.Name)},{Escape(e.Category)},{Escape(e.FamilyName)}"));
                    File.WriteAllLines(path, lines);
                });

                Dispatcher.Invoke(() => StatusMessage = $"Exported to {path}");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusMessage = $"Export failed: {ex.Message}");
            }
        }

        private string Escape(string s) => $"\"{s?.Replace("\"", "\"\"")}\"";

        private void CleanupCts()
        {
            _cts?.Dispose();
            _cts = null;
        }

        private void RaiseAllCommandsCanExecute()
        {
            RefreshCommand.RaiseCanExecuteChanged();
            ApplyCommand.RaiseCanExecuteChanged();
            ExportCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }

        public override void Dispose()
        {
            _handler.OnSelectionReceived -= HandleSelectionReceived;
            _handler.OnOperationComplete -= HandleOperationComplete;
            _handler.OnError -= HandleError;
            CleanupCts();
            base.Dispose();
        }
    }
}
```

### View (XAML)

```xml
<mah:MetroWindow x:Class="MyTool.Views.ToolWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:mah="http://metro.mahapps.com/winfx/xaml/controls"
        xmlns:iconPacks="http://metro.mahapps.com/winfx/xaml/iconpacks"
        Title="My Tool" Height="500" Width="700"
        GlowBrush="{DynamicResource MahApps.Brushes.Accent}"
        BorderThickness="1">

    <Window.InputBindings>
        <KeyBinding Key="F5" Command="{Binding RefreshCommand}"/>
        <KeyBinding Key="Escape" Command="{Binding CancelCommand}"/>
        <KeyBinding Key="S" Modifiers="Control" Command="{Binding ApplyCommand}"/>
    </Window.InputBindings>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Toolbar -->
        <ToolBar Grid.Row="0">
            <Button Command="{Binding RefreshCommand}" ToolTip="Refresh (F5)">
                <StackPanel Orientation="Horizontal">
                    <iconPacks:PackIconMaterial Kind="Refresh" Width="16" Height="16"/>
                    <TextBlock Text="Refresh" Margin="4,0,0,0"/>
                </StackPanel>
            </Button>
            <Separator/>
            <Button Command="{Binding ApplyCommand}" ToolTip="Apply Changes (Ctrl+S)">
                <StackPanel Orientation="Horizontal">
                    <iconPacks:PackIconMaterial Kind="Check" Width="16" Height="16"/>
                    <TextBlock Text="Apply" Margin="4,0,0,0"/>
                </StackPanel>
            </Button>
            <Button Command="{Binding ExportCommand}" ToolTip="Export to CSV">
                <StackPanel Orientation="Horizontal">
                    <iconPacks:PackIconMaterial Kind="Export" Width="16" Height="16"/>
                    <TextBlock Text="Export" Margin="4,0,0,0"/>
                </StackPanel>
            </Button>
        </ToolBar>

        <!-- Main Content -->
        <DataGrid Grid.Row="1" 
                  ItemsSource="{Binding Elements}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  SelectionMode="Extended"
                  IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBoolConverter}}">
            <DataGrid.Columns>
                <DataGridTextColumn Header="ID" Binding="{Binding Id}" Width="80"/>
                <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="*"/>
                <DataGridTextColumn Header="Category" Binding="{Binding Category}" Width="120"/>
                <DataGridTextColumn Header="Family" Binding="{Binding FamilyName}" Width="150"/>
            </DataGrid.Columns>
        </DataGrid>

        <!-- Status Bar -->
        <StatusBar Grid.Row="2">
            <StatusBarItem>
                <TextBlock Text="{Binding StatusMessage}"/>
            </StatusBarItem>
            <StatusBarItem HorizontalAlignment="Right" Width="250">
                <Grid>
                    <ProgressBar Value="{Binding Progress}" 
                                 Minimum="0" Maximum="100" Height="18"
                                 Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}"/>
                    <Button Content="Cancel" 
                            Command="{Binding CancelCommand}"
                            HorizontalAlignment="Right"
                            Margin="0,0,4,0"
                            Padding="8,2"
                            Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}"/>
                </Grid>
            </StatusBarItem>
        </StatusBar>
    </Grid>
</mah:MetroWindow>
```

---

## Example 2: AI-Assisted Tool (Async Pattern)

Shows correct flow for AI/HTTP calls - capture context in Handler, async work in ViewModel.

### Handler - Capture Context Only

```csharp
public class AIAssistHandler : IExternalEventHandler
{
    private AIRequest _request;
    private readonly object _lock = new();

    public event Action<RevitContextSnapshot> OnContextCaptured;

    public void SetRequest(AIRequest request)
    {
        lock (_lock) { _request = request; }
    }

    public void Execute(UIApplication app)
    {
        AIRequest request;
        lock (_lock) { request = _request; _request = null; }
        if (request?.Type != AIRequestType.CaptureContext) return;

        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;

        // Capture minimal context - NO async work here
        var snapshot = new RevitContextSnapshot
        {
            DocumentTitle = doc?.Title,
            DocumentPath = doc?.PathName,
            ActiveViewName = doc?.ActiveView?.Name,
            ActiveViewType = doc?.ActiveView?.ViewType.ToString(),
            SelectedElementIds = uidoc?.Selection.GetElementIds()
                .Select(id => id.IntegerValue).ToList() ?? new(),
            SelectedElementSummaries = uidoc?.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .Take(50) // Limit for AI context
                .Select(e => new ElementSummary
                {
                    Id = e.Id.IntegerValue,
                    Name = e.Name,
                    Category = e.Category?.Name,
                    TypeName = e.GetType().Name
                }).ToList() ?? new()
        };

        // Fire callback and RETURN - no blocking
        OnContextCaptured?.Invoke(snapshot);
    }

    public string GetName() => "AI Assist Context Capture";
}

public class RevitContextSnapshot
{
    public string DocumentTitle { get; set; }
    public string DocumentPath { get; set; }
    public string ActiveViewName { get; set; }
    public string ActiveViewType { get; set; }
    public List<int> SelectedElementIds { get; set; }
    public List<ElementSummary> SelectedElementSummaries { get; set; }
}

public class ElementSummary
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string TypeName { get; set; }
}
```

### ViewModel - Async AI Call

```csharp
public class AIAssistViewModel : ViewModelBase
{
    private readonly ExternalEvent _externalEvent;
    private readonly AIAssistHandler _handler;
    private readonly IAIService _aiService;
    private CancellationTokenSource _cts;

    private string _userPrompt;
    public string UserPrompt
    {
        get => _userPrompt;
        set => SetProperty(ref _userPrompt, value);
    }

    private string _aiResponse;
    public string AIResponse
    {
        get => _aiResponse;
        set => SetProperty(ref _aiResponse, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { SetProperty(ref _isBusy, value); AnalyzeCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand CancelCommand { get; }

    public AIAssistViewModel(ExternalEvent externalEvent, AIAssistHandler handler, IAIService aiService)
    {
        _externalEvent = externalEvent;
        _handler = handler;
        _aiService = aiService;

        _handler.OnContextCaptured += HandleContextCaptured;

        AnalyzeCommand = new RelayCommand(ExecuteAnalyze, _ => !IsBusy && !string.IsNullOrWhiteSpace(UserPrompt));
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
    }

    private void ExecuteAnalyze(object _)
    {
        IsBusy = true;
        AIResponse = "Capturing Revit context...";
        _cts = new CancellationTokenSource();

        // Step 1: Request context capture from Revit
        _handler.SetRequest(new AIRequest { Type = AIRequestType.CaptureContext });
        _externalEvent.Raise();
        // Handler will fire OnContextCaptured callback
    }

    private async void HandleContextCaptured(RevitContextSnapshot snapshot)
    {
        // Step 2: Now on callback, run async AI work
        try
        {
            await Dispatcher.InvokeAsync(() => AIResponse = "Analyzing with AI...");

            // Build prompt with context
            var fullPrompt = BuildPrompt(snapshot);

            // Async HTTP/AI call - OUTSIDE of ExternalEvent handler
            var response = await _aiService.ChatAsync(fullPrompt, _cts.Token);

            await Dispatcher.InvokeAsync(() =>
            {
                AIResponse = response;
                IsBusy = false;
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AIResponse = "Cancelled.";
                IsBusy = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AIResponse = $"Error: {ex.Message}";
                IsBusy = false;
            });
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private string BuildPrompt(RevitContextSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Document: {snapshot.DocumentTitle}");
        sb.AppendLine($"Active View: {snapshot.ActiveViewName} ({snapshot.ActiveViewType})");
        sb.AppendLine($"Selected Elements: {snapshot.SelectedElementIds.Count}");
        
        if (snapshot.SelectedElementSummaries.Any())
        {
            sb.AppendLine("\nSelection Summary:");
            foreach (var elem in snapshot.SelectedElementSummaries.Take(10))
            {
                sb.AppendLine($"  - {elem.Name} ({elem.Category})");
            }
        }

        sb.AppendLine($"\nUser Question: {UserPrompt}");
        return sb.ToString();
    }

    public override void Dispose()
    {
        _handler.OnContextCaptured -= HandleContextCaptured;
        _cts?.Cancel();
        _cts?.Dispose();
        base.Dispose();
    }
}
```

---

## Example 3: Validation with Error Display

### ViewModel with Validation

```csharp
public class InputViewModel : ViewModelBase, IDataErrorInfo
{
    private string _parameterName;
    public string ParameterName
    {
        get => _parameterName;
        set { SetProperty(ref _parameterName, value); ValidateAll(); }
    }

    private double _parameterValue;
    public double ParameterValue
    {
        get => _parameterValue;
        set { SetProperty(ref _parameterValue, value); ValidateAll(); }
    }

    private readonly Dictionary<string, string> _errors = new();

    public string Error => _errors.Values.FirstOrDefault();

    public string this[string columnName]
    {
        get
        {
            _errors.TryGetValue(columnName, out var error);
            return error;
        }
    }

    public bool HasErrors => _errors.Any();

    private void ValidateAll()
    {
        _errors.Clear();

        if (string.IsNullOrWhiteSpace(ParameterName))
            _errors["ParameterName"] = "Parameter name is required";
        else if (ParameterName.Length > 100)
            _errors["ParameterName"] = "Parameter name too long (max 100 chars)";

        if (ParameterValue < 0)
            _errors["ParameterValue"] = "Value must be positive";
        else if (ParameterValue > 1000000)
            _errors["ParameterValue"] = "Value exceeds maximum (1,000,000)";

        OnPropertyChanged(nameof(HasErrors));
        ApplyCommand?.RaiseCanExecuteChanged();
    }

    public RelayCommand ApplyCommand { get; }

    public InputViewModel()
    {
        ApplyCommand = new RelayCommand(ExecuteApply, _ => !HasErrors && !IsBusy);
    }
}
```

### XAML Validation Template

```xml
<TextBox Text="{Binding ParameterName, UpdateSourceTrigger=PropertyChanged, ValidatesOnDataErrors=True}"
         mah:TextBoxHelper.Watermark="Parameter name"
         mah:TextBoxHelper.UseFloatingWatermark="True">
    <TextBox.Style>
        <Style TargetType="TextBox" BasedOn="{StaticResource MahApps.Styles.TextBox}">
            <Style.Triggers>
                <Trigger Property="Validation.HasError" Value="True">
                    <Setter Property="ToolTip" 
                            Value="{Binding (Validation.Errors)[0].ErrorContent, RelativeSource={RelativeSource Self}}"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </TextBox.Style>
</TextBox>

<mah:NumericUpDown Value="{Binding ParameterValue, ValidatesOnDataErrors=True}"
                   Minimum="0" Maximum="1000000"
                   mah:TextBoxHelper.Watermark="Value"/>

<!-- Error summary -->
<TextBlock Text="{Binding Error}" 
           Foreground="{DynamicResource MahApps.Brushes.Validation5}"
           Visibility="{Binding HasErrors, Converter={StaticResource BoolToVisibilityConverter}}"
           Margin="0,8,0,0"/>
```

---

## Example 4: Busy Overlay Pattern

### Overlay Control

```xml
<Grid>
    <!-- Main content -->
    <ContentControl Content="{Binding}" ContentTemplate="{StaticResource MainContentTemplate}"/>
    
    <!-- Busy overlay -->
    <Border Background="#80000000" 
            Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}}">
        <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
            <mah:ProgressRing IsActive="{Binding IsBusy}" Width="60" Height="60"/>
            <TextBlock Text="{Binding StatusMessage}" 
                       Foreground="White" 
                       FontSize="14"
                       Margin="0,16,0,0"
                       HorizontalAlignment="Center"/>
            <Button Content="Cancel" 
                    Command="{Binding CancelCommand}"
                    Margin="0,16,0,0"
                    Padding="24,8"/>
        </StackPanel>
    </Border>
</Grid>
```

---

## Example 5: Selection Sync Pattern

Keep ViewModel in sync with Revit selection using periodic refresh.

```csharp
public class SelectionSyncViewModel : ViewModelBase
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly ExternalEvent _externalEvent;
    private readonly SelectionHandler _handler;

    public ObservableCollection<ElementInfo> CurrentSelection { get; } = new();

    public SelectionSyncViewModel(ExternalEvent externalEvent, SelectionHandler handler)
    {
        _externalEvent = externalEvent;
        _handler = handler;
        _handler.OnSelectionChanged += HandleSelectionChanged;

        // Periodic refresh (not real-time subscription - that's complex in Revit)
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!IsBusy)
            {
                _handler.SetRequest(SelectionRequest.Refresh());
                _externalEvent.Raise();
            }
        };
    }

    public void StartSync() => _refreshTimer.Start();
    public void StopSync() => _refreshTimer.Stop();

    private void HandleSelectionChanged(List<ElementInfo> elements)
    {
        Dispatcher.Invoke(() =>
        {
            // Only update if different (avoid flickering)
            var currentIds = CurrentSelection.Select(e => e.Id).ToHashSet();
            var newIds = elements.Select(e => e.Id).ToHashSet();
            
            if (!currentIds.SetEquals(newIds))
            {
                CurrentSelection.Clear();
                foreach (var e in elements) CurrentSelection.Add(e);
            }
        });
    }

    public override void Dispose()
    {
        _refreshTimer.Stop();
        _handler.OnSelectionChanged -= HandleSelectionChanged;
        base.Dispose();
    }
}
```
