---
name: modeless-wpf-external-event
description: Scaffold modeless WPF tools with MVVM + ExternalEvent pattern for Revit-safe UI. Use when creating modeless dialogs, WPF windows that stay open while working in Revit, or any tool that needs UI-triggered Revit actions.
---

# Modeless WPF Tool Pattern (MVVM + ExternalEvent)

Scaffold Revit-safe modeless WPF tools where ViewModel never calls Revit API directly.

## Required Inputs

| Input | Example | Required |
|-------|---------|----------|
| Namespace | `DCM.ParameterEditor` | Yes |
| Tool name | `ParameterEditor` | Yes |
| Request types | `GetSelection`, `SetValue`, `Export` | Yes |
| Async operations? | AI/HTTP/file I/O | No (affects flow) |

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│  WPF UI Thread                                              │
│  ┌──────────┐      ┌─────────────┐      ┌───────────────┐   │
│  │   View   │ ←──→ │  ViewModel  │ ───→ │ RequestQueue  │   │
│  └──────────┘      └─────────────┘      └───────┬───────┘   │
│                           ↑                     │           │
│                           │ Callback            │ Raise()   │
└───────────────────────────│─────────────────────│───────────┘
                            │                     ↓
┌───────────────────────────│─────────────────────────────────┐
│  Revit Main Thread        │                                 │
│                    ┌──────┴──────┐                          │
│                    │   Handler    │  ← IExternalEventHandler│
│                    │  (Revit API) │                         │
│                    └─────────────┘                          │
└─────────────────────────────────────────────────────────────┘
```

## Step-by-Step Implementation

### Step 1: Request DTOs

Create immutable request types with factory methods.

```csharp
namespace {{Namespace}}.Requests
{
    public enum RequestType
    {
        None,
        {{RequestType1}},
        {{RequestType2}}
    }

    public sealed class {{Tool}}Request
    {
        public RequestType Type { get; }
        public IReadOnlyList<ElementId> ElementIds { get; init; }
        // Add other payload properties as needed

        private {{Tool}}Request(RequestType type) => Type = type;

        // Factory methods
        public static {{Tool}}Request {{RequestType1}}() 
            => new(RequestType.{{RequestType1}});
            
        public static {{Tool}}Request {{RequestType2}}(IEnumerable<ElementId> ids, /* params */) 
            => new(RequestType.{{RequestType2}}) { ElementIds = ids.ToList() };
    }
}
```

### Step 2: ExternalEvent Handler

Handler is the **ONLY** place that calls Revit API.

```csharp
namespace {{Namespace}}.Handlers
{
    public class {{Tool}}Handler : IExternalEventHandler
    {
        private {{Tool}}Request _request;
        private readonly object _lock = new();

        // Thread-safe callbacks to ViewModel
        public event Action<List<ElementId>> OnSelectionReceived;
        public event Action<string> OnError;

        public void SetRequest({{Tool}}Request request)
        {
            lock (_lock) { _request = request; }
        }

        public void Execute(UIApplication app)
        {
            {{Tool}}Request request;
            lock (_lock) { request = _request; _request = null; }
            if (request == null) return;

            try
            {
                switch (request.Type)
                {
                    case RequestType.{{RequestType1}}:
                        Execute{{RequestType1}}(app);
                        break;
                    case RequestType.{{RequestType2}}:
                        Execute{{RequestType2}}(app, request);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
        }

        private void Execute{{RequestType1}}(UIApplication app)
        {
            // Revit API calls here - read operations
            var ids = app.ActiveUIDocument?.Selection.GetElementIds().ToList();
            OnSelectionReceived?.Invoke(ids ?? new List<ElementId>());
        }

        private void Execute{{RequestType2}}(UIApplication app, {{Tool}}Request request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;

            using var tx = new Transaction(doc, "{{Tool}} Operation");
            tx.Start();
            // Modify elements using request.ElementIds, request.Payload
            tx.Commit();
        }

        public string GetName() => "{{Namespace}}.{{Tool}}Handler";
    }
}
```

### Step 3: ViewModel

ViewModel builds requests, raises ExternalEvent, never touches Revit API.

```csharp
namespace {{Namespace}}.ViewModels
{
    public class {{Tool}}ViewModel : ViewModelBase
    {
        private readonly ExternalEvent _externalEvent;
        private readonly {{Tool}}Handler _handler;

        private bool _isBusy;
        private string _statusMessage = "Ready";

        public {{Tool}}ViewModel(ExternalEvent externalEvent, {{Tool}}Handler handler)
        {
            _externalEvent = externalEvent;
            _handler = handler;

            // Subscribe to handler callbacks
            _handler.OnSelectionReceived += HandleSelectionReceived;
            _handler.OnError += msg => { IsBusy = false; StatusMessage = $"Error: {msg}"; };

            // Commands
            {{RequestType1}}Command = new RelayCommand(Execute{{RequestType1}}, _ => !IsBusy);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { SetProperty(ref _isBusy, value); {{RequestType1}}Command.RaiseCanExecuteChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public RelayCommand {{RequestType1}}Command { get; }

        private void Execute{{RequestType1}}(object _)
        {
            IsBusy = true;
            StatusMessage = "Working...";
            _handler.SetRequest({{Tool}}Request.{{RequestType1}}());
            _externalEvent.Raise();
        }

        private void HandleSelectionReceived(List<ElementId> ids)
        {
            // Update on UI thread if needed: Dispatcher.BeginInvoke(...)
            IsBusy = false;
            StatusMessage = $"Received {ids.Count} elements";
        }

        public void Cleanup()
        {
            _handler.OnSelectionReceived -= HandleSelectionReceived;
        }
    }
}
```

### Step 4: Window Manager (Single Instance)

```csharp
namespace {{Namespace}}
{
    internal static class {{Tool}}Manager
    {
        private static {{Tool}}Window _window;
        private static ExternalEvent _externalEvent;
        private static {{Tool}}Handler _handler;

        public static void ShowOrFocus(UIApplication uiapp)
        {
            if (_window != null && _window.IsVisible)
            {
                _window.Activate();
                return;
            }

            _handler ??= new {{Tool}}Handler();
            _externalEvent ??= ExternalEvent.Create(_handler);

            var vm = new {{Tool}}ViewModel(_externalEvent, _handler);
            _window = new {{Tool}}Window { DataContext = vm };

            new WindowInteropHelper(_window).Owner = uiapp.MainWindowHandle;
            _window.Closed += (_, _) => { vm.Cleanup(); _window = null; };
            _window.Show();
        }
    }
}
```

### Step 5: Entry Point

Wire to Entry class for pyRevit launcher.

```csharp
public static class Entry
{
    public static void Show{{Tool}}(UIApplication uiapp)
    {
        {{Tool}}Manager.ShowOrFocus(uiapp);
    }
}
```

## Async Operations Flow (AI/HTTP/File I/O)

When ViewModel needs async operations (AI calls, HTTP, file I/O):

```
1. ExternalEvent captures minimal context (IDs, names - NOT Element refs)
2. Handler returns immediately (releases Revit thread)
3. ViewModel runs async Task with CancellationToken
4. Update UI via Dispatcher
5. If result needs Revit changes → queue another request → Raise()
```

```csharp
// In Handler - capture snapshot only
private void ExecuteGetContext(UIApplication app)
{
    var snapshot = new RevitContextSnapshot
    {
        DocumentTitle = app.ActiveUIDocument?.Document?.Title,
        SelectedIds = app.ActiveUIDocument?.Selection.GetElementIds().ToList()
    };
    OnContextCaptured?.Invoke(snapshot);
}

// In ViewModel - async work AFTER handler returns
private async void HandleContextCaptured(RevitContextSnapshot snapshot)
{
    IsBusy = true;
    try
    {
        // Async HTTP/AI call - NOT in ExternalEvent handler!
        var result = await _aiService.AnalyzeAsync(snapshot, _cts.Token);
        
        await Dispatcher.BeginInvoke(() => {
            ResultText = result.Summary;
            IsBusy = false;
        });
    }
    catch (OperationCanceledException) { /* user cancelled */ }
}
```

## Output Checklist

```
Architecture:
- [ ] ViewModel never calls Revit API
- [ ] Only Handler.Execute() touches Revit API
- [ ] Transactions kept minimal in handler

Request Pattern:
- [ ] Immutable request DTOs with factory methods
- [ ] RequestType enum covers all operations
- [ ] Handler switches by request type

UI Responsiveness:
- [ ] IsBusy property disables commands during operation
- [ ] StatusMessage provides feedback
- [ ] Cancel button for long operations

Thread Safety:
- [ ] Handler uses lock for request access
- [ ] Callbacks marshal to UI thread when needed
- [ ] Async operations use CancellationToken

Lifecycle:
- [ ] Single-instance window pattern
- [ ] Cleanup unsubscribes handler events
- [ ] ExternalEvent created once, reused
```

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Calling `doc.GetElement()` in ViewModel | Move to Handler, pass IDs not Elements |
| Running HTTP in Handler.Execute() | Capture snapshot, return, then async in VM |
| Multiple window instances | Use Manager with static `_window` check |
| Blocking UI with `Task.Wait()` | Use async/await, update via Dispatcher |
| Holding Element references | Store `ElementId`, fetch Element fresh in Handler |

## Additional Resources

- For complete working example, see [examples.md](examples.md)
- For Entry class scaffolding, see `scaffold-entry-contract` skill
