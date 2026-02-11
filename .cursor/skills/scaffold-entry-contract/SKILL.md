---
name: scaffold-entry-contract
description: Scaffold stable C# Entry class API for pyRevit DLL loading, with optional ExternalCommand debug wrapper. Use when creating a DLL entrypoint for pyRevit, setting up Entry.Run/ShowTool/ShowTicketAssistant methods, or needing F5 debug wrapper for Revit add-in development.
---

# Scaffold C# Entry Contract + ExternalCommand Wrapper

Generate a stable DLL API that pyRevit thin launchers call. Optionally include an ExternalCommand wrapper for F5 debugging in Visual Studio.

## Required Inputs

| Input | Example | Required |
|-------|---------|----------|
| Namespace | `DCM.TicketAssistant` | Yes |
| Entry methods needed | `Run`, `ShowTool`, `ShowTicketAssistant` | Yes |
| Include debug wrapper? | Yes/No | No (default: Yes) |
| Modeless window class | `ToolWindow` | If ShowTool/ShowTicketAssistant |

## Entry Class Contract (Always Generate)

The Entry class is the **stable API surface** that pyRevit calls. Keep it minimal and stable.

```csharp
using Autodesk.Revit.UI;
using System;

namespace {{Namespace}}
{
    /// <summary>
    /// Stable entry point for pyRevit launcher.
    /// Do NOT change method signatures - this is a public contract.
    /// </summary>
    public static class Entry
    {
        /// <summary>
        /// Generic command entry point.
        /// </summary>
        public static void Run(UIApplication uiapp)
        {
            if (uiapp == null)
                throw new ArgumentNullException(nameof(uiapp));

            try
            {
                // TODO: Implement command logic or delegate to service
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User cancelled - silent exit
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show(
                    "Error",
                    $"{{Namespace}}.Entry.Run failed:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Show modeless tool window.
        /// </summary>
        public static void ShowTool(UIApplication uiapp)
        {
            if (uiapp == null)
                throw new ArgumentNullException(nameof(uiapp));

            try
            {
                ToolWindowManager.ShowOrFocus(uiapp);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show(
                    "Error",
                    $"{{Namespace}}.Entry.ShowTool failed:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Show ticket assistant window.
        /// </summary>
        public static void ShowTicketAssistant(UIApplication uiapp)
        {
            if (uiapp == null)
                throw new ArgumentNullException(nameof(uiapp));

            try
            {
                TicketAssistantManager.ShowOrFocus(uiapp);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show(
                    "Error",
                    $"{{Namespace}}.Entry.ShowTicketAssistant failed:\n{ex.Message}");
            }
        }
    }
}
```

## Modeless Window Manager Pattern

For `ShowTool` and `ShowTicketAssistant`, use a manager class to handle single-instance windows with ExternalEvent:

```csharp
using Autodesk.Revit.UI;
using System;
using System.Windows.Interop;

namespace {{Namespace}}
{
    /// <summary>
    /// Manages single-instance modeless window lifecycle.
    /// </summary>
    internal static class ToolWindowManager
    {
        private static ToolWindow _window;
        private static ExternalEvent _externalEvent;
        private static ToolRequestHandler _handler;

        public static void ShowOrFocus(UIApplication uiapp)
        {
            // If window exists and is visible, bring to front
            if (_window != null && _window.IsVisible)
            {
                _window.Activate();
                return;
            }

            // Create handler and ExternalEvent (once per session)
            if (_handler == null)
            {
                _handler = new ToolRequestHandler();
                _externalEvent = ExternalEvent.Create(_handler);
            }

            // Create ViewModel with ExternalEvent access
            var viewModel = new ToolViewModel(_externalEvent, _handler);

            // Create and configure window
            _window = new ToolWindow { DataContext = viewModel };
            
            // Set owner to Revit main window
            var hwndHelper = new WindowInteropHelper(_window);
            hwndHelper.Owner = uiapp.MainWindowHandle;

            // Wire up cleanup on close
            _window.Closed += (s, e) => _window = null;

            _window.Show();
        }
    }
}
```

## ExternalEvent Handler Template

```csharp
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace {{Namespace}}
{
    public class ToolRequestHandler : IExternalEventHandler
    {
        public RequestType CurrentRequest { get; set; } = RequestType.None;
        public object RequestData { get; set; }
        
        /// <summary>
        /// Callback to notify ViewModel when execution completes.
        /// </summary>
        public Action<object> OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            if (app?.ActiveUIDocument == null)
                return;

            Document doc = app.ActiveUIDocument.Document;
            object result = null;

            try
            {
                switch (CurrentRequest)
                {
                    case RequestType.CollectElements:
                        result = ExecuteCollectElements(doc);
                        break;

                    case RequestType.ModifyElements:
                        result = ExecuteModifyElements(doc);
                        break;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Handler Error", ex.Message);
            }
            finally
            {
                CurrentRequest = RequestType.None;
                OnCompleted?.Invoke(result);
            }
        }

        private object ExecuteCollectElements(Document doc)
        {
            // Read-only operation - no transaction needed
            // TODO: Implement collection logic
            return null;
        }

        private object ExecuteModifyElements(Document doc)
        {
            using (Transaction tx = new Transaction(doc, "Modify Elements"))
            {
                tx.Start();
                // TODO: Implement modification logic using RequestData
                tx.Commit();
            }
            return null;
        }

        public string GetName() => "{{Namespace}}.ToolRequestHandler";
    }

    public enum RequestType
    {
        None,
        CollectElements,
        ModifyElements
    }
}
```

## Debug Wrapper (Optional - for F5 Debugging)

Use this only for local development. Do NOT ship with pyRevit extension.

```csharp
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace {{Namespace}}.Debug
{
    /// <summary>
    /// ExternalCommand wrapper for F5 debugging in Visual Studio.
    /// Requires .addin manifest pointing to this class.
    /// DO NOT ship this - pyRevit uses Entry class directly.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DebugRunCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            Entry.Run(commandData.Application);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class DebugShowToolCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            Entry.ShowTool(commandData.Application);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class DebugShowTicketAssistantCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            Entry.ShowTicketAssistant(commandData.Application);
            return Result.Succeeded;
        }
    }
}
```

## Debug .addin Manifest

Place in `%AppData%\Autodesk\Revit\Addins\{{RevitVersion}}\` for local debugging only:

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Text>Debug - Run</Text>
    <Description>Debug wrapper for Entry.Run</Description>
    <Assembly>{{DllPath}}</Assembly>
    <FullClassName>{{Namespace}}.Debug.DebugRunCommand</FullClassName>
    <ClientId>{{NewGuid1}}</ClientId>
    <VendorId>{{VendorId}}</VendorId>
  </AddIn>
  
  <AddIn Type="Command">
    <Text>Debug - Show Tool</Text>
    <Description>Debug wrapper for Entry.ShowTool</Description>
    <Assembly>{{DllPath}}</Assembly>
    <FullClassName>{{Namespace}}.Debug.DebugShowToolCommand</FullClassName>
    <ClientId>{{NewGuid2}}</ClientId>
    <VendorId>{{VendorId}}</VendorId>
  </AddIn>
  
  <AddIn Type="Command">
    <Text>Debug - Ticket Assistant</Text>
    <Description>Debug wrapper for Entry.ShowTicketAssistant</Description>
    <Assembly>{{DllPath}}</Assembly>
    <FullClassName>{{Namespace}}.Debug.DebugShowTicketAssistantCommand</FullClassName>
    <ClientId>{{NewGuid3}}</ClientId>
    <VendorId>{{VendorId}}</VendorId>
  </AddIn>
</RevitAddIns>
```

## Placeholder Substitutions

| Placeholder | Replace With | Example |
|-------------|--------------|---------|
| `{{Namespace}}` | Your DLL namespace | `DCM.TicketAssistant` |
| `{{DllPath}}` | Absolute path to debug DLL | `D:\Projects\bin\Debug\net48\MyTool.dll` |
| `{{RevitVersion}}` | Target Revit year | `2024` |
| `{{VendorId}}` | Your vendor ID | `DCMvn` |
| `{{NewGuid1/2/3}}` | Fresh GUIDs | Use `[Guid]::NewGuid()` in PowerShell |

## Output Checklist

```
Entry API Contract:
- [ ] Entry class is public static
- [ ] All entry methods accept UIApplication as single parameter
- [ ] Method signatures match workspace rules (Run, ShowTool, ShowTicketAssistant)
- [ ] Null guard on uiapp parameter
- [ ] Exception handling shows actionable error dialog

Modeless Window (if applicable):
- [ ] Single-instance pattern via manager class
- [ ] ExternalEvent + Handler created once
- [ ] Window owner set to Revit main window handle
- [ ] Cleanup on window close

Debug Wrapper (if included):
- [ ] Commands in separate Debug namespace
- [ ] [Transaction(TransactionMode.Manual)] attribute
- [ ] Each command delegates to corresponding Entry method
- [ ] .addin manifest has unique GUIDs per command

Minimal Dependencies:
- [ ] Entry class only depends on Revit API
- [ ] No third-party dependencies in Entry signature
- [ ] Internal implementation details hidden from Entry surface
```

## File Organization

Recommended file structure:

```
src/
  Entry.cs                    # Public API contract
  ToolWindowManager.cs        # Modeless window lifecycle
  ToolRequestHandler.cs       # ExternalEvent handler
  ViewModels/
    ToolViewModel.cs
  Views/
    ToolWindow.xaml
    ToolWindow.xaml.cs
  Debug/                      # Only for development
    DebugCommands.cs          # ExternalCommand wrappers
```

Keep `Entry.cs` minimal and stable. Implementation details go in manager/handler classes.
