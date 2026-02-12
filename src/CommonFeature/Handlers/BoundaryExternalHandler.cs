using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CommonFeature.Graphics;
using CommonFeature.Models;
using CommonFeature.Views;

namespace CommonFeature.Handlers
{
    /// <summary>
    /// External event handler specifically for Boundary feature operations.
    /// 
    /// Design Note:
    /// This handler is SEPARATE from CommonFeatureHandler because:
    /// 1. BoundaryWindow needs its own ExternalEvent for independent operations
    /// 2. Pick elements, update preview, and clear preview are boundary-specific
    /// 3. DirectContext3D server lifecycle is tied to this window
    /// 
    /// Each modeless window that needs Revit API access should have its own handler.
    /// </summary>
    public class BoundaryExternalHandler : IExternalEventHandler
    {
        #region Request Types
        
        /// <summary>
        /// Types of requests this handler can process.
        /// </summary>
        public enum RequestType
        {
            /// <summary>No pending request.</summary>
            None,
            
            /// <summary>Pick elements from Revit model.</summary>
            PickElements,
            
            /// <summary>Update preview graphics based on current settings.</summary>
            UpdatePreview,
            
            /// <summary>Clear all preview graphics.</summary>
            ClearPreview
        }

        #endregion

        #region Fields

        private RequestType _request = RequestType.None;
        private BoundaryDisplaySettings _settings;
        private readonly object _lock = new();

        #endregion

        #region Properties

        /// <summary>
        /// Reference to the BoundaryWindow for callbacks.
        /// </summary>
        public BoundaryWindow Window { get; set; }
        
        /// <summary>
        /// Reference to the DirectContext3D graphics server.
        /// </summary>
        public BoundaryGraphicsServer GraphicsServer { get; set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Queue a request for execution on the Revit main thread.
        /// </summary>
        /// <param name="request">Type of request to execute.</param>
        /// <param name="settings">Optional display settings (for UpdatePreview).</param>
        public void SetRequest(RequestType request, BoundaryDisplaySettings settings = null)
        {
            lock (_lock)
            {
                _request = request;
                _settings = settings;
            }
        }

        #endregion

        #region IExternalEventHandler Implementation

        /// <summary>
        /// Execute the pending request on Revit's main thread.
        /// Called by Revit when ExternalEvent.Raise() is triggered.
        /// </summary>
        public void Execute(UIApplication app)
        {
            RequestType request;
            BoundaryDisplaySettings settings;
            
            lock (_lock)
            {
                request = _request;
                settings = _settings;
                _request = RequestType.None;
                _settings = null;
            }

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;
            var doc = uidoc.Document;

            try
            {
                switch (request)
                {
                    case RequestType.PickElements:
                        ExecutePickElements(uidoc);
                        break;
                    case RequestType.UpdatePreview:
                        ExecuteUpdatePreview(doc, settings);
                        break;
                    case RequestType.ClearPreview:
                        ExecuteClearPreview(doc);
                        break;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User cancelled picking - restore window
                RestoreWindow();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Boundary Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handler name for Revit's external event system.
        /// </summary>
        public string GetName() => "BoundaryExternalHandler";

        #endregion

        #region Private Methods - Request Execution

        private void ExecutePickElements(UIDocument uidoc)
        {
            try
            {
                var refs = uidoc.Selection.PickObjects(
                    ObjectType.Element, 
                    "Select elements to show boundary (ESC to finish)");

                var elementIds = refs.Select(r => r.ElementId.Value).ToList();
                
                // Update window with picked elements
                Window?.Dispatcher.Invoke(() =>
                {
                    Window.WindowState = WindowState.Normal;
                    Window.Activate();
                    Window.OnElementsPicked(elementIds);
                });
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User cancelled - just restore window without updating selection
                RestoreWindow();
            }
        }

        private void ExecuteUpdatePreview(Document doc, BoundaryDisplaySettings settings)
        {
            if (settings == null || settings.ElementIds.Count == 0) return;
            
            // Guard: Check document validity
            if (doc == null || !doc.IsValidObject) return;
            
            // Limit number of elements to prevent memory issues
            const int MaxElements = 500;
            var elementIds = settings.ElementIds;
            if (elementIds.Count > MaxElements)
            {
                MessageBox.Show(
                    $"Too many elements selected ({elementIds.Count}).\nShowing first {MaxElements} elements only.",
                    "Performance Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                elementIds = elementIds.Take(MaxElements).ToList();
            }
            
            // Calculate boundary data for each element
            var boundaryDataList = new List<ElementBoundaryData>();
            
            foreach (var id in elementIds)
            {
                try
                {
                    var elemId = new ElementId(id);
                    var elem = doc.GetElement(elemId);
                    
                    // Skip if element is null or invalid (e.g., from linked file)
                    if (elem == null) continue;
                    if (!elem.IsValidObject) continue;
                    
                    var boundaryData = CalculateBoundaryData(doc, elem, settings.UseRotatedBoundingBox);
                    if (boundaryData != null)
                    {
                        boundaryDataList.Add(boundaryData);
                    }
                }
                catch
                {
                    // Skip elements that cause errors
                    continue;
                }
            }
            
            if (boundaryDataList.Count == 0) return;
            
            // Update graphics server
            if (GraphicsServer != null)
            {
                GraphicsServer.UpdateData(boundaryDataList, settings);
                RefreshActiveView(doc);
            }
        }

        private void ExecuteClearPreview(Document doc)
        {
            if (GraphicsServer != null)
            {
                GraphicsServer.ClearData();
                RefreshActiveView(doc);
            }
        }

        #endregion

        #region Private Methods - Helpers

        private void RestoreWindow()
        {
            Window?.Dispatcher.Invoke(() =>
            {
                if (Window != null)
                {
                    Window.WindowState = WindowState.Normal;
                    Window.Activate();
                }
            });
        }

        private void RefreshActiveView(Document doc)
        {
            // Force view refresh to update DirectContext3D graphics
            try
            {
                var uidoc = new UIDocument(doc);
                uidoc.RefreshActiveView();
            }
            catch
            {
                // Ignore refresh errors
            }
        }

        /// <summary>
        /// Calculate boundary data for a single element.
        /// </summary>
        private ElementBoundaryData CalculateBoundaryData(Document doc, Element elem, bool useRotated)
        {
            // Guard: Check element validity
            if (elem == null || !elem.IsValidObject) return null;
            
            // Get bounding box - always world-aligned first
            BoundingBoxXYZ bbox = null;
            Transform rotationTransform = null;
            
            try
            {
                bbox = elem.get_BoundingBox(null);
            }
            catch
            {
                // Some elements don't support BoundingBox
                return null;
            }
            
            if (bbox == null) return null;
            
            // Validate bounding box has valid dimensions
            if (bbox.Min == null || bbox.Max == null) return null;
            if (bbox.Min.IsAlmostEqualTo(bbox.Max)) return null; // Zero-size bbox
            
            // For rotated mode, get element's transform if available
            if (useRotated)
            {
                rotationTransform = GetElementTransform(elem);
            }
            
            // Calculate centroid from world-aligned bounding box
            var centroid = new XYZ(
                (bbox.Min.X + bbox.Max.X) / 2,
                (bbox.Min.Y + bbox.Max.Y) / 2,
                (bbox.Min.Z + bbox.Max.Z) / 2);
            
            return new ElementBoundaryData
            {
                ElementId = elem.Id.Value,
                BoundingBox = bbox,
                RotationTransform = rotationTransform,
                MinPoint = bbox.Min,
                MaxPoint = bbox.Max,
                Centroid = centroid
            };
        }
        
        /// <summary>
        /// Get element's transform for rotation visualization.
        /// Returns null if element has no meaningful rotation.
        /// </summary>
        private Transform GetElementTransform(Element elem)
        {
            try
            {
                // FamilyInstance - most common case with rotation
                if (elem is FamilyInstance fi)
                {
                    var transform = fi.GetTransform();
                    if (transform != null && !transform.IsIdentity)
                    {
                        return transform;
                    }
                }
                
                // Wall - get orientation from LocationCurve
                if (elem is Wall wall)
                {
                    if (wall.Location is LocationCurve locCurve)
                    {
                        var curve = locCurve.Curve;
                        if (curve is Line line)
                        {
                            var direction = line.Direction.Normalize();
                            var origin = line.GetEndPoint(0);
                            return Transform.CreateTranslation(origin);
                        }
                    }
                }
                
                // LocationPoint elements with rotation
                if (elem.Location is LocationPoint locPt)
                {
                    double rotation = locPt.Rotation;
                    if (Math.Abs(rotation) > 0.001) // Has rotation
                    {
                        return Transform.CreateRotation(XYZ.BasisZ, rotation);
                    }
                }
            }
            catch
            {
                // Ignore errors getting transform
            }
            
            return null;
        }

        #endregion
    }
}
