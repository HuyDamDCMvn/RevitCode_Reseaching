using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace HDRevitLib
{
    /// <summary>
    /// Utility class for interactive element picking from Revit UI.
    /// All methods handle user cancellation gracefully.
    /// </summary>
    public static class PickElement
    {
        #region Pick Single Element

        /// <summary>
        /// Pick a single element with optional filter.
        /// Returns null if user cancels or nothing selected.
        /// </summary>
        public static Element Single(UIDocument uidoc, string statusPrompt = "Select an element")
        {
            if (uidoc == null) return null;

            try
            {
                var reference = uidoc.Selection.PickObject(ObjectType.Element, statusPrompt);
                return uidoc.Document.GetElement(reference);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick a single element with a selection filter.
        /// Returns null if user cancels or nothing selected.
        /// </summary>
        public static Element Single(
            UIDocument uidoc,
            ISelectionFilter filter,
            string statusPrompt = "Select an element")
        {
            if (uidoc == null) return null;

            try
            {
                var reference = uidoc.Selection.PickObject(ObjectType.Element, filter, statusPrompt);
                return uidoc.Document.GetElement(reference);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick a single element of a specific type.
        /// Returns null if user cancels or nothing selected.
        /// </summary>
        public static T Single<T>(UIDocument uidoc, string statusPrompt = null) where T : Element
        {
            if (uidoc == null) return null;

            var prompt = statusPrompt ?? $"Select a {typeof(T).Name}";
            var filter = new ClassSelectionFilter<T>();

            try
            {
                var reference = uidoc.Selection.PickObject(ObjectType.Element, filter, prompt);
                return uidoc.Document.GetElement(reference) as T;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick a single element of specific categories.
        /// Returns null if user cancels or nothing selected.
        /// </summary>
        public static Element SingleOfCategory(
            UIDocument uidoc,
            string statusPrompt,
            params BuiltInCategory[] categories)
        {
            if (uidoc == null) return null;

            var filter = new CategorySelectionFilter(categories);

            try
            {
                var reference = uidoc.Selection.PickObject(ObjectType.Element, filter, statusPrompt);
                return uidoc.Document.GetElement(reference);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        #endregion

        #region Pick Multiple Elements

        /// <summary>
        /// Pick multiple elements.
        /// Returns empty list if user cancels.
        /// </summary>
        public static IList<Element> Multiple(UIDocument uidoc, string statusPrompt = "Select elements")
        {
            if (uidoc == null) return new List<Element>();

            try
            {
                var references = uidoc.Selection.PickObjects(ObjectType.Element, statusPrompt);
                return references
                    .Select(r => uidoc.Document.GetElement(r))
                    .Where(e => e != null)
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<Element>();
            }
        }

        /// <summary>
        /// Pick multiple elements with a selection filter.
        /// Returns empty list if user cancels.
        /// </summary>
        public static IList<Element> Multiple(
            UIDocument uidoc,
            ISelectionFilter filter,
            string statusPrompt = "Select elements")
        {
            if (uidoc == null) return new List<Element>();

            try
            {
                var references = uidoc.Selection.PickObjects(ObjectType.Element, filter, statusPrompt);
                return references
                    .Select(r => uidoc.Document.GetElement(r))
                    .Where(e => e != null)
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<Element>();
            }
        }

        /// <summary>
        /// Pick multiple elements of a specific type.
        /// Returns empty list if user cancels.
        /// </summary>
        public static IList<T> Multiple<T>(UIDocument uidoc, string statusPrompt = null) where T : Element
        {
            if (uidoc == null) return new List<T>();

            var prompt = statusPrompt ?? $"Select {typeof(T).Name}s";
            var filter = new ClassSelectionFilter<T>();

            try
            {
                var references = uidoc.Selection.PickObjects(ObjectType.Element, filter, prompt);
                return references
                    .Select(r => uidoc.Document.GetElement(r) as T)
                    .Where(e => e != null)
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<T>();
            }
        }

        /// <summary>
        /// Pick multiple elements of specific categories.
        /// Returns empty list if user cancels.
        /// </summary>
        public static IList<Element> MultipleOfCategory(
            UIDocument uidoc,
            string statusPrompt,
            params BuiltInCategory[] categories)
        {
            if (uidoc == null) return new List<Element>();

            var filter = new CategorySelectionFilter(categories);

            try
            {
                var references = uidoc.Selection.PickObjects(ObjectType.Element, filter, statusPrompt);
                return references
                    .Select(r => uidoc.Document.GetElement(r))
                    .Where(e => e != null)
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<Element>();
            }
        }

        #endregion

        #region Pick Point

        /// <summary>
        /// Pick a point in 3D space.
        /// Returns null if user cancels.
        /// </summary>
        public static XYZ Point(UIDocument uidoc, string statusPrompt = "Pick a point")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickPoint(statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick a point with snap settings.
        /// Returns null if user cancels.
        /// </summary>
        public static XYZ Point(UIDocument uidoc, ObjectSnapTypes snapTypes, string statusPrompt = "Pick a point")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickPoint(snapTypes, statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        #endregion

        #region Pick Box

        /// <summary>
        /// Pick a rectangular box by two corner points.
        /// Returns null if user cancels.
        /// </summary>
        public static PickedBox Box(UIDocument uidoc, string statusPrompt = "Pick two corners")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickBox(PickBoxStyle.Directional, statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick elements within a rectangular box selection.
        /// Returns empty list if user cancels.
        /// </summary>
        public static IList<Element> ByBox(UIDocument uidoc, string statusPrompt = "Draw selection box")
        {
            if (uidoc == null) return new List<Element>();

            try
            {
                var box = uidoc.Selection.PickBox(PickBoxStyle.Crossing, statusPrompt);
                if (box == null) return new List<Element>();

                var min = box.Min;
                var max = box.Max;

                // Create outline for intersection filter
                var outline = new Outline(
                    new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));

                var bbFilter = new BoundingBoxIntersectsFilter(outline);

                return new FilteredElementCollector(uidoc.Document, uidoc.ActiveView.Id)
                    .WherePasses(bbFilter)
                    .WhereElementIsNotElementType()
                    .ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return new List<Element>();
            }
        }

        #endregion

        #region Pick Face/Edge

        /// <summary>
        /// Pick a face on an element.
        /// Returns null if user cancels.
        /// </summary>
        public static Reference Face(UIDocument uidoc, string statusPrompt = "Select a face")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickObject(ObjectType.Face, statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick a face with a selection filter.
        /// Returns null if user cancels.
        /// </summary>
        public static Reference Face(
            UIDocument uidoc,
            ISelectionFilter filter,
            string statusPrompt = "Select a face")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickObject(ObjectType.Face, filter, statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick an edge on an element.
        /// Returns null if user cancels.
        /// </summary>
        public static Reference Edge(UIDocument uidoc, string statusPrompt = "Select an edge")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickObject(ObjectType.Edge, statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Pick an edge with a selection filter.
        /// Returns null if user cancels.
        /// </summary>
        public static Reference Edge(
            UIDocument uidoc,
            ISelectionFilter filter,
            string statusPrompt = "Select an edge")
        {
            if (uidoc == null) return null;

            try
            {
                return uidoc.Selection.PickObject(ObjectType.Edge, filter, statusPrompt);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        #endregion

        #region Current Selection

        /// <summary>
        /// Get currently selected elements.
        /// Returns empty list if nothing selected.
        /// </summary>
        public static IList<Element> GetSelected(UIDocument uidoc)
        {
            if (uidoc == null) return new List<Element>();

            var ids = uidoc.Selection.GetElementIds();
            if (ids == null || ids.Count == 0) return new List<Element>();

            return ids
                .Select(id => uidoc.Document.GetElement(id))
                .Where(e => e != null)
                .ToList();
        }

        /// <summary>
        /// Get currently selected elements of a specific type.
        /// Returns empty list if nothing selected or no matching elements.
        /// </summary>
        public static IList<T> GetSelected<T>(UIDocument uidoc) where T : Element
        {
            if (uidoc == null) return new List<T>();

            var ids = uidoc.Selection.GetElementIds();
            if (ids == null || ids.Count == 0) return new List<T>();

            return ids
                .Select(id => uidoc.Document.GetElement(id))
                .OfType<T>()
                .ToList();
        }

        /// <summary>
        /// Set the current selection.
        /// </summary>
        public static void SetSelected(UIDocument uidoc, ICollection<ElementId> ids)
        {
            if (uidoc == null || ids == null) return;
            uidoc.Selection.SetElementIds(ids);
        }

        /// <summary>
        /// Set the current selection from elements.
        /// </summary>
        public static void SetSelected(UIDocument uidoc, IEnumerable<Element> elements)
        {
            if (uidoc == null || elements == null) return;

            var ids = elements
                .Where(e => e != null)
                .Select(e => e.Id)
                .ToList();

            uidoc.Selection.SetElementIds(ids);
        }

        /// <summary>
        /// Clear the current selection.
        /// </summary>
        public static void ClearSelection(UIDocument uidoc)
        {
            if (uidoc == null) return;
            uidoc.Selection.SetElementIds(new List<ElementId>());
        }

        #endregion

        #region Pre-selected or Pick

        /// <summary>
        /// Get pre-selected elements or prompt user to pick.
        /// </summary>
        public static IList<Element> GetOrPick(UIDocument uidoc, string statusPrompt = "Select elements")
        {
            if (uidoc == null) return new List<Element>();

            var preSelected = GetSelected(uidoc);
            if (preSelected.Count > 0) return preSelected;

            return Multiple(uidoc, statusPrompt);
        }

        /// <summary>
        /// Get pre-selected elements of type or prompt user to pick.
        /// </summary>
        public static IList<T> GetOrPick<T>(UIDocument uidoc, string statusPrompt = null) where T : Element
        {
            if (uidoc == null) return new List<T>();

            var preSelected = GetSelected<T>(uidoc);
            if (preSelected.Count > 0) return preSelected;

            return Multiple<T>(uidoc, statusPrompt);
        }

        #endregion
    }
}
