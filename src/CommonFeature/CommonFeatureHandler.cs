using System;
using System.Collections.Generic;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommonFeature.Models;
using CommonFeature.Views;

namespace CommonFeature
{
    /// <summary>
    /// Request types for CommonFeature operations.
    /// </summary>
    public enum RequestType
    {
        None,
        Isolate,
        GetInformation,
        ShowParameter,
        ShowBoundary
    }

    /// <summary>
    /// Request DTO for CommonFeature operations.
    /// </summary>
    public sealed class CommonFeatureRequest
    {
        public RequestType Type { get; }
        
        private CommonFeatureRequest(RequestType type) => Type = type;

        public static CommonFeatureRequest Isolate() => new(RequestType.Isolate);
        public static CommonFeatureRequest GetInformation() => new(RequestType.GetInformation);
        public static CommonFeatureRequest ShowParameter() => new(RequestType.ShowParameter);
        public static CommonFeatureRequest ShowBoundary() => new(RequestType.ShowBoundary);
    }

    /// <summary>
    /// External event handler - ONLY place that calls Revit API.
    /// </summary>
    public class CommonFeatureHandler : IExternalEventHandler
    {
        private CommonFeatureRequest _request;
        private readonly object _lock = new();

        /// <summary>
        /// Callback when operation completes.
        /// </summary>
        public event Action<string> OnOperationCompleted;

        /// <summary>
        /// Callback on error.
        /// </summary>
        public event Action<string> OnError;

        public void SetRequest(CommonFeatureRequest request)
        {
            lock (_lock) { _request = request; }
        }

        public void Execute(UIApplication app)
        {
            CommonFeatureRequest request;
            lock (_lock) { request = _request; _request = null; }
            if (request == null) return;

            try
            {
                switch (request.Type)
                {
                    case RequestType.Isolate:
                        ExecuteIsolate(app);
                        break;
                    case RequestType.GetInformation:
                        ExecuteGetInformation(app);
                        break;
                    case RequestType.ShowParameter:
                        ExecuteShowParameter(app);
                        break;
                    case RequestType.ShowBoundary:
                        ExecuteShowBoundary(app);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
        }

        private void ExecuteIsolate(UIApplication app)
        {
            // TODO: Implement Isolate feature
            OnOperationCompleted?.Invoke("Isolate: On Developing");
        }

        private void ExecuteGetInformation(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var selection = uidoc.Selection.GetElementIds();

            if (selection.Count == 0)
            {
                OnError?.Invoke("Please select at least one element");
                return;
            }

            // Collect element information
            var elementInfos = new List<ElementInfo>();

            foreach (var elementId in selection)
            {
                var element = doc.GetElement(elementId);
                if (element == null) continue;

                var info = GetElementInfo(doc, element);
                elementInfos.Add(info);
            }

            // Show window on UI thread
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var infoWindow = new InfoWindow();
                infoWindow.SetData(elementInfos);
                infoWindow.Show();
            });

            OnOperationCompleted?.Invoke($"Loaded {elementInfos.Count} element(s)");
        }

        private ElementInfo GetElementInfo(Document doc, Element element)
        {
            // Get Element ID
            long id = element.Id.Value;

            // Get Family Name and Type
            string familyName = "-";
            string familyType = "-";

            if (element is FamilyInstance fi)
            {
                var symbol = fi.Symbol;
                if (symbol != null)
                {
                    familyName = symbol.Family?.Name ?? "-";
                    familyType = symbol.Name ?? "-";
                }
            }
            else if (element.GetTypeId() != ElementId.InvalidElementId)
            {
                var elementType = doc.GetElement(element.GetTypeId());
                if (elementType != null)
                {
                    familyType = elementType.Name ?? "-";
                    
                    // Try to get family name from type
                    var familyNameParam = elementType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (familyNameParam != null && familyNameParam.HasValue)
                    {
                        familyName = familyNameParam.AsString() ?? "-";
                    }
                    else
                    {
                        familyName = elementType.GetType().Name;
                    }
                }
            }
            else
            {
                familyName = element.GetType().Name;
                familyType = element.Name ?? "-";
            }

            // Get Category
            string category = element.Category?.Name ?? "-";

            // Get Workset
            string workset = "-";
            if (doc.IsWorkshared)
            {
                var worksetId = element.WorksetId;
                if (worksetId != WorksetId.InvalidWorksetId)
                {
                    var ws = doc.GetWorksetTable().GetWorkset(worksetId);
                    workset = ws?.Name ?? "-";
                }
            }

            return new ElementInfo(id, familyName, familyType, category, workset);
        }

        private void ExecuteShowParameter(UIApplication app)
        {
            // TODO: Implement Show Parameter feature
            OnOperationCompleted?.Invoke("Show Parameter: On Developing");
        }

        private void ExecuteShowBoundary(UIApplication app)
        {
            // TODO: Implement Show Boundary feature
            OnOperationCompleted?.Invoke("Show Boundary: On Developing");
        }

        public string GetName() => "CommonFeature.Handler";
    }
}
