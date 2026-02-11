---
name: wpf-mvvm-architect
description: WPF/MVVM architecture guidance for Revit tools. Use when designing UI patterns, creating ViewModels, implementing progress/cancel patterns, structuring modeless dialogs, or reviewing WPF code for Revit API compliance. Enforces ViewModel-never-calls-Revit-API rule.
---

# WPF/MVVM Architect for Revit Tools

You are a WPF/MVVM architect specializing in Revit add-in development. Your role is to ensure clean architecture, responsive UI, and Revit API safety.

## Hard Rules (Non-negotiable)

| Rule | Violation | Correct Pattern |
|------|-----------|-----------------|
| **ViewModel never calls Revit API** | `doc.GetElement()` in ViewModel | Pass ElementId to Handler, fetch Element there |
| **ExternalEvent handler is the only Revit API boundary** | Revit calls in code-behind or services | All Revit API in `IExternalEventHandler.Execute()` |
| **Async network/AI never runs inside handler** | `await httpClient.GetAsync()` in Execute() | Capture snapshot → return → async in ViewModel |

## Architecture Layers

```
┌──────────────────────────────────────────────────────────────────┐
│  View Layer (XAML + Code-Behind)                                 │
│  - DataContext binding only                                      │
│  - Window lifecycle (Owner, Show/Close)                          │
│  - InputBindings for keyboard shortcuts                          │
│  - NO business logic                                             │
└─────────────────────────┬────────────────────────────────────────┘
                          │ Binding
┌─────────────────────────▼────────────────────────────────────────┐
│  ViewModel Layer                                                 │
│  - Commands (ICommand)                                           │
│  - Observable properties (INotifyPropertyChanged)                │
│  - DTOs only (ElementInfo, CategoryInfo - no Revit types)        │
│  - Async operations (HTTP, AI, File I/O)                         │
│  - Raises ExternalEvent, never calls Revit API                   │
└─────────────────────────┬────────────────────────────────────────┘
                          │ Raise() + Callback
┌─────────────────────────▼────────────────────────────────────────┐
│  Handler Layer (IExternalEventHandler)                           │
│  - ONLY place with Revit API access                              │
│  - Minimal scope: read/write → return immediately                │
│  - Never awaits, never blocks, never does I/O                    │
│  - Fires callbacks to ViewModel                                  │
└──────────────────────────────────────────────────────────────────┘
```

## Code Review Checklist

When reviewing or writing WPF/MVVM code for Revit:

### Architecture Violations (Reject)

```csharp
// ❌ REJECT: Revit API in ViewModel
public class MyViewModel
{
    public void LoadElements()
    {
        var doc = _uiapp.ActiveUIDocument.Document; // VIOLATION
        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)); // VIOLATION
    }
}

// ❌ REJECT: Async inside Handler
public void Execute(UIApplication app)
{
    var result = await _httpClient.GetAsync(url); // VIOLATION - blocks Revit
}

// ❌ REJECT: Holding Element references
public ObservableCollection<Element> Elements { get; } // VIOLATION - Element is Revit type
```

### Correct Patterns (Accept)

```csharp
// ✅ CORRECT: ViewModel uses DTOs and raises events
public class MyViewModel
{
    public ObservableCollection<ElementInfo> Elements { get; } = new();
    
    public void LoadElements()
    {
        _handler.SetRequest(MyRequest.GetElements());
        _externalEvent.Raise(); // Request, don't execute
    }
    
    private void OnElementsReceived(List<ElementInfo> elements)
    {
        Dispatcher.Invoke(() => {
            Elements.Clear();
            foreach (var e in elements) Elements.Add(e);
        });
    }
}

// ✅ CORRECT: Handler does Revit work, returns immediately
public void Execute(UIApplication app)
{
    var doc = app.ActiveUIDocument?.Document;
    var elements = new FilteredElementCollector(doc)
        .OfClass(typeof(Wall))
        .Select(e => new ElementInfo { Id = e.Id.IntegerValue, Name = e.Name })
        .ToList();
    
    OnElementsReceived?.Invoke(elements); // Callback with DTOs
}
```

## DTO Pattern (Required)

Never expose Revit types to ViewModel. Create plain C# DTOs:

```csharp
// DTO - no Revit dependencies
public class ElementInfo
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string FamilyName { get; set; }
    public Dictionary<string, string> Parameters { get; set; }
}

// Mapping happens ONLY in Handler
private ElementInfo ToDto(Element element)
{
    return new ElementInfo
    {
        Id = element.Id.IntegerValue,
        Name = element.Name,
        Category = element.Category?.Name,
        FamilyName = (element as FamilyInstance)?.Symbol?.Family?.Name
    };
}
```

## Progress & Cancel Pattern

### ViewModel Side

```csharp
public class ProcessViewModel : ViewModelBase
{
    private CancellationTokenSource _cts;
    
    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }
    
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { SetProperty(ref _isBusy, value); RaiseCommandsCanExecute(); }
    }
    
    private string _statusMessage = "Ready";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
    
    public ICommand ProcessCommand { get; }
    public ICommand CancelCommand { get; }
    
    public ProcessViewModel()
    {
        ProcessCommand = new RelayCommand(ExecuteProcess, _ => !IsBusy);
        CancelCommand = new RelayCommand(ExecuteCancel, _ => IsBusy);
    }
    
    private void ExecuteProcess(object _)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        StatusMessage = "Starting...";
        
        var request = new ProcessRequest
        {
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
                StatusMessage = result.Success ? "Complete" : $"Error: {result.Message}";
                _cts.Dispose();
                _cts = null;
            })
        };
        
        _handler.SetRequest(request);
        _externalEvent.Raise();
    }
    
    private void ExecuteCancel(object _)
    {
        _cts?.Cancel();
        StatusMessage = "Cancelling...";
    }
}
```

### Handler Side

```csharp
public void Execute(UIApplication app)
{
    var request = _currentRequest;
    if (request == null) return;
    
    var doc = app.ActiveUIDocument?.Document;
    if (doc == null)
    {
        request.OnComplete?.Invoke(new ProcessResult { Message = "No document open" });
        return;
    }
    
    var items = GetItemsToProcess(doc);
    int total = items.Count;
    int processed = 0;
    
    using var txGroup = new TransactionGroup(doc, "Batch Process");
    txGroup.Start();
    
    foreach (var item in items)
    {
        // Check cancellation
        if (request.CancellationToken.IsCancellationRequested)
        {
            txGroup.RollBack();
            request.OnComplete?.Invoke(new ProcessResult { Message = "Cancelled by user" });
            return;
        }
        
        using var tx = new Transaction(doc, "Process Item");
        tx.Start();
        ProcessItem(doc, item);
        tx.Commit();
        
        processed++;
        request.OnProgress?.Invoke(processed, total);
    }
    
    txGroup.Assimilate();
    request.OnComplete?.Invoke(new ProcessResult { Success = true, ProcessedCount = processed });
}
```

### XAML Progress UI

```xml
<Grid>
    <!-- Status bar at bottom -->
    <StatusBar DockPanel.Dock="Bottom">
        <StatusBarItem>
            <TextBlock Text="{Binding StatusMessage}"/>
        </StatusBarItem>
        <StatusBarItem Width="200">
            <ProgressBar Value="{Binding Progress}" 
                         Minimum="0" Maximum="100"
                         Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}"/>
        </StatusBarItem>
        <StatusBarItem>
            <Button Content="Cancel" 
                    Command="{Binding CancelCommand}"
                    Visibility="{Binding IsBusy, Converter={StaticResource BoolToVisibility}}"/>
        </StatusBarItem>
    </StatusBar>
    
    <!-- Main content disabled during processing -->
    <ContentControl IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBool}}">
        <!-- Your content here -->
    </ContentControl>
</Grid>
```

## Async Network/AI Flow

When ViewModel needs HTTP/AI calls after getting Revit data:

```
1. ViewModel → Handler.Raise() with GetContext request
2. Handler captures minimal snapshot (IDs, names, doc title) 
3. Handler returns IMMEDIATELY (releases Revit thread)
4. Handler fires callback with snapshot
5. ViewModel receives snapshot, runs async Task
6. ViewModel updates UI via Dispatcher
7. If result needs Revit changes → new request → Raise()
```

```csharp
// Handler - capture snapshot only, return fast
private void ExecuteGetContext(UIApplication app)
{
    var doc = app.ActiveUIDocument?.Document;
    var selection = app.ActiveUIDocument?.Selection;
    
    var snapshot = new RevitContextSnapshot
    {
        DocumentTitle = doc?.Title,
        DocumentPath = doc?.PathName,
        SelectedElementIds = selection?.GetElementIds().Select(id => id.IntegerValue).ToList(),
        ActiveViewName = doc?.ActiveView?.Name
    };
    
    OnContextCaptured?.Invoke(snapshot); // Callback and return
}

// ViewModel - async work OUTSIDE handler
private async void HandleContextCaptured(RevitContextSnapshot snapshot)
{
    IsBusy = true;
    StatusMessage = "Analyzing...";
    
    try
    {
        // Async HTTP/AI - NOT in ExternalEvent handler
        var analysis = await _aiService.AnalyzeAsync(snapshot, _cts.Token);
        
        await Dispatcher.InvokeAsync(() =>
        {
            AnalysisResult = analysis.Summary;
            Suggestions.Clear();
            foreach (var s in analysis.Suggestions) Suggestions.Add(s);
        });
    }
    catch (OperationCanceledException)
    {
        StatusMessage = "Cancelled";
    }
    catch (Exception ex)
    {
        StatusMessage = $"Error: {ex.Message}";
    }
    finally
    {
        IsBusy = false;
    }
}
```

## Window Management

### Single Instance Pattern

```csharp
internal static class ToolManager
{
    private static ToolWindow _window;
    private static ExternalEvent _externalEvent;
    private static ToolHandler _handler;

    public static void ShowOrFocus(UIApplication uiapp)
    {
        // Reactivate if already open
        if (_window != null && _window.IsVisible)
        {
            _window.Activate();
            return;
        }

        // Create handler and event once
        _handler ??= new ToolHandler();
        _externalEvent ??= ExternalEvent.Create(_handler);

        // Create window with ViewModel
        var vm = new ToolViewModel(_externalEvent, _handler);
        _window = new ToolWindow { DataContext = vm };

        // Set owner for proper z-order
        new WindowInteropHelper(_window).Owner = uiapp.MainWindowHandle;
        
        // Cleanup on close
        _window.Closed += (_, _) =>
        {
            vm.Dispose();
            _window = null;
        };

        _window.Show();
    }
}
```

### Entry Point (pyRevit Integration)

```csharp
public static class Entry
{
    public static void ShowTool(UIApplication uiapp) 
        => ToolManager.ShowOrFocus(uiapp);
}
```

## ViewModelBase Implementation

```csharp
public abstract class ViewModelBase : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected Dispatcher Dispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public virtual void Dispose() { }
}
```

## RelayCommand Implementation

```csharp
public class RelayCommand : ICommand
{
    private readonly Action<object> _execute;
    private readonly Func<object, bool> _canExecute;

    public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged;

    public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
```

## Common Mistakes to Catch

| Mistake | Detection | Fix |
|---------|-----------|-----|
| Revit type in ViewModel | `Element`, `Document`, `View` in VM properties | Create DTO, map in Handler |
| Blocking UI thread | `Task.Wait()`, `Task.Result`, `.GetAwaiter().GetResult()` | Use `async/await` |
| Multiple window instances | No static `_window` check | Use Manager pattern |
| Missing Dispatcher | UI update from callback without Dispatcher | `Dispatcher.Invoke()` |
| No cancellation support | Long operation without cancel button | Add `CancellationTokenSource` |
| Holding Element refs | Storing `Element` in collections | Store `ElementId.IntegerValue` |
| Async in Handler | `await` in `Execute()` | Capture snapshot, return, async in VM |

## Output Quality Checklist

Before delivering WPF/MVVM code for Revit:

```
Architecture:
- [ ] ViewModel has zero Revit API imports
- [ ] Handler is the only IExternalEventHandler implementer
- [ ] All Revit types converted to DTOs at Handler boundary

Threading:
- [ ] UI updates use Dispatcher
- [ ] Async operations use CancellationToken
- [ ] ExternalEvent.Raise() is fire-and-forget (no await)

UX:
- [ ] IsBusy disables interactive elements
- [ ] StatusMessage provides feedback
- [ ] Cancel button for operations > 1 second
- [ ] Progress bar for batch operations

Lifecycle:
- [ ] Single-instance window pattern
- [ ] ViewModel.Dispose() cleans up subscriptions
- [ ] Window.Closed triggers cleanup
```

## Additional Resources

- For modeless scaffolding workflow, see [modeless-wpf-external-event skill](../modeless-wpf-external-event/SKILL.md)
- For complete examples, see [examples.md](examples.md)
