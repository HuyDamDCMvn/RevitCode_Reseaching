using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace HD.Core.Services
{
    /// <summary>
    /// Service for view operations: isolate, zoom, section box, etc.
    /// </summary>
    public static class ViewService
    {
        #region Isolate

        /// <summary>
        /// Isolate elements temporarily in the active view.
        /// Resets previous isolation before applying new one.
        /// </summary>
        public static bool IsolateElements(UIDocument uidoc, List<long> elementIds, string transactionName = "Isolate Elements")
        {
            if (uidoc == null || elementIds == null || elementIds.Count == 0) return false;

            var doc = uidoc.Document;
            var activeView = uidoc.ActiveView;
            if (activeView == null) return false;

            var ids = elementIds.Select(id => new ElementId(id)).ToList();

            using (var trans = new Transaction(doc, transactionName))
            {
                trans.Start();
                try
                {
                    // Reset previous isolation first
                    activeView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    
                    // Apply new isolation
                    activeView.IsolateElementsTemporary(ids);
                    
                    trans.Commit();
                    return true;
                }
                catch
                {
                    trans.RollBack();
                    return false;
                }
            }
        }

        /// <summary>
        /// Reset temporary isolation in active view.
        /// </summary>
        public static bool ResetIsolation(UIDocument uidoc)
        {
            if (uidoc == null) return false;

            var doc = uidoc.Document;
            var activeView = uidoc.ActiveView;
            if (activeView == null) return false;

            using (var trans = new Transaction(doc, "Reset Isolation"))
            {
                trans.Start();
                try
                {
                    activeView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    trans.Commit();
                    return true;
                }
                catch
                {
                    trans.RollBack();
                    return false;
                }
            }
        }

        #endregion

        #region Zoom

        /// <summary>
        /// Zoom to fit elements in the active view.
        /// </summary>
        public static bool ZoomToElements(UIDocument uidoc, List<long> elementIds)
        {
            if (uidoc == null || elementIds == null || elementIds.Count == 0) return false;

            try
            {
                var ids = elementIds.Select(id => new ElementId(id)).ToList();
                uidoc.ShowElements(ids);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Refresh the active view (e.g. after DirectContext3D updates).
        /// </summary>
        public static bool RefreshView(UIDocument uidoc)
        {
            if (uidoc == null) return false;

            try
            {
                uidoc.RefreshActiveView();
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Section Box

        /// <summary>
        /// Create a section box around elements in a 3D view.
        /// </summary>
        public static bool CreateSectionBox(UIDocument uidoc, List<long> elementIds, double padding = 1.0)
        {
            if (uidoc == null || elementIds == null || elementIds.Count == 0) return false;

            var doc = uidoc.Document;
            var view3D = uidoc.ActiveView as View3D;
            if (view3D == null || view3D.IsPerspective) return false;

            // Calculate bounding box
            BoundingBoxXYZ combinedBox = null;

            foreach (var id in elementIds)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem == null) continue;

                var box = elem.get_BoundingBox(view3D) ?? elem.get_BoundingBox(null);
                if (box == null) continue;

                if (combinedBox == null)
                {
                    combinedBox = new BoundingBoxXYZ
                    {
                        Min = box.Min,
                        Max = box.Max
                    };
                }
                else
                {
                    combinedBox.Min = new XYZ(
                        Math.Min(combinedBox.Min.X, box.Min.X),
                        Math.Min(combinedBox.Min.Y, box.Min.Y),
                        Math.Min(combinedBox.Min.Z, box.Min.Z));
                    combinedBox.Max = new XYZ(
                        Math.Max(combinedBox.Max.X, box.Max.X),
                        Math.Max(combinedBox.Max.Y, box.Max.Y),
                        Math.Max(combinedBox.Max.Z, box.Max.Z));
                }
            }

            if (combinedBox == null) return false;

            // Add padding
            var paddingVec = new XYZ(padding, padding, padding);
            combinedBox.Min -= paddingVec;
            combinedBox.Max += paddingVec;

            using (var trans = new Transaction(doc, "Create Section Box"))
            {
                trans.Start();
                try
                {
                    view3D.SetSectionBox(combinedBox);
                    trans.Commit();

                    // Zoom to section box
                    uidoc.RefreshActiveView();
                    return true;
                }
                catch
                {
                    trans.RollBack();
                    return false;
                }
            }
        }

        #endregion

        #region Selection

        /// <summary>
        /// Select elements in the active view.
        /// </summary>
        public static bool SelectElements(UIDocument uidoc, List<long> elementIds)
        {
            if (uidoc == null || elementIds == null) return false;

            try
            {
                var ids = elementIds.Select(id => new ElementId(id)).ToList();
                uidoc.Selection.SetElementIds(ids);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get currently selected element IDs.
        /// </summary>
        public static List<long> GetSelectedIds(UIDocument uidoc)
        {
            if (uidoc?.Selection == null) return new List<long>();
            return uidoc.Selection.GetElementIds().Select(id => id.Value).ToList();
        }

        #endregion

        #region View Info

        /// <summary>
        /// Get the name of the active view.
        /// </summary>
        public static string GetActiveViewName(UIDocument uidoc)
        {
            try
            {
                return uidoc?.ActiveView?.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Check if active view is a 3D view.
        /// </summary>
        public static bool IsActive3DView(UIDocument uidoc)
        {
            return uidoc?.ActiveView is View3D v3d && !v3d.IsPerspective;
        }

        #endregion
    }
}
