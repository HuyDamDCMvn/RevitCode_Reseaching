using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace HDRevitLib
{
    /// <summary>
    /// Utility class for filtering and collecting elements from Revit documents.
    /// Follows early-filtering pattern for performance.
    /// </summary>
    public static class FilterElement
    {
        #region By Class

        /// <summary>
        /// Get all elements of a specific type from the document.
        /// </summary>
        public static IList<T> OfClass<T>(Document doc) where T : Element
        {
            if (doc == null) return new List<T>();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .ToList();
        }

        /// <summary>
        /// Get all elements of a specific type from a view.
        /// </summary>
        public static IList<T> OfClass<T>(Document doc, ElementId viewId) where T : Element
        {
            if (doc == null || viewId == null || viewId == ElementId.InvalidElementId)
                return new List<T>();

            return new FilteredElementCollector(doc, viewId)
                .OfClass(typeof(T))
                .Cast<T>()
                .ToList();
        }

        #endregion

        #region By Category

        /// <summary>
        /// Get all elements of a specific built-in category.
        /// </summary>
        public static IList<Element> OfCategory(Document doc, BuiltInCategory category)
        {
            if (doc == null) return new List<Element>();

            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();
        }

        /// <summary>
        /// Get all elements of a specific built-in category from a view.
        /// </summary>
        public static IList<Element> OfCategory(Document doc, ElementId viewId, BuiltInCategory category)
        {
            if (doc == null || viewId == null || viewId == ElementId.InvalidElementId)
                return new List<Element>();

            return new FilteredElementCollector(doc, viewId)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();
        }

        /// <summary>
        /// Get all elements of specific categories.
        /// </summary>
        public static IList<Element> OfCategories(Document doc, ICollection<BuiltInCategory> categories)
        {
            if (doc == null || categories == null || categories.Count == 0)
                return new List<Element>();

            var categoryFilter = new ElementMulticategoryFilter(categories);
            return new FilteredElementCollector(doc)
                .WherePasses(categoryFilter)
                .WhereElementIsNotElementType()
                .ToList();
        }

        #endregion

        #region By Family

        /// <summary>
        /// Get all family instances of a specific family name.
        /// </summary>
        public static IList<FamilyInstance> OfFamilyName(Document doc, string familyName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(familyName))
                return new List<FamilyInstance>();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Family?.Name == familyName)
                .ToList();
        }

        /// <summary>
        /// Get all family instances of a specific family symbol (type).
        /// </summary>
        public static IList<FamilyInstance> OfFamilySymbol(Document doc, ElementId symbolId)
        {
            if (doc == null || symbolId == null || symbolId == ElementId.InvalidElementId)
                return new List<FamilyInstance>();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Id == symbolId)
                .ToList();
        }

        #endregion

        #region Element Types

        /// <summary>
        /// Get all element types of a specific class.
        /// </summary>
        public static IList<T> GetTypes<T>(Document doc) where T : ElementType
        {
            if (doc == null) return new List<T>();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .ToList();
        }

        /// <summary>
        /// Get all family symbols (types) of a specific category.
        /// </summary>
        public static IList<FamilySymbol> GetFamilySymbols(Document doc, BuiltInCategory category)
        {
            if (doc == null) return new List<FamilySymbol>();

            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();
        }

        #endregion

        #region Walls, Floors, Roofs (Common)

        /// <summary>
        /// Get all walls in the document.
        /// </summary>
        public static IList<Wall> GetWalls(Document doc)
        {
            return OfClass<Wall>(doc);
        }

        /// <summary>
        /// Get all floors in the document.
        /// </summary>
        public static IList<Floor> GetFloors(Document doc)
        {
            return OfClass<Floor>(doc);
        }

        /// <summary>
        /// Get all roofs in the document.
        /// </summary>
        public static IList<RoofBase> GetRoofs(Document doc)
        {
            return OfClass<RoofBase>(doc);
        }

        /// <summary>
        /// Get all doors in the document.
        /// </summary>
        public static IList<Element> GetDoors(Document doc)
        {
            return OfCategory(doc, BuiltInCategory.OST_Doors);
        }

        /// <summary>
        /// Get all windows in the document.
        /// </summary>
        public static IList<Element> GetWindows(Document doc)
        {
            return OfCategory(doc, BuiltInCategory.OST_Windows);
        }

        #endregion

        #region By Parameter

        /// <summary>
        /// Get elements where a parameter equals a specific value.
        /// </summary>
        public static IList<Element> ByParameterValue(
            Document doc,
            BuiltInCategory category,
            BuiltInParameter parameter,
            string value)
        {
            if (doc == null || string.IsNullOrEmpty(value))
                return new List<Element>();

            var rule = ParameterFilterRuleFactory.CreateEqualsRule(
                new ElementId(parameter), value, false);

            var filter = new ElementParameterFilter(rule);

            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .ToList();
        }

        /// <summary>
        /// Get elements where a parameter contains a specific string.
        /// </summary>
        public static IList<Element> ByParameterContains(
            Document doc,
            BuiltInCategory category,
            BuiltInParameter parameter,
            string searchText)
        {
            if (doc == null || string.IsNullOrEmpty(searchText))
                return new List<Element>();

            var rule = ParameterFilterRuleFactory.CreateContainsRule(
                new ElementId(parameter), searchText, false);

            var filter = new ElementParameterFilter(rule);

            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .ToList();
        }

        #endregion

        #region By Level

        /// <summary>
        /// Get all elements on a specific level.
        /// </summary>
        public static IList<Element> OnLevel(Document doc, ElementId levelId)
        {
            if (doc == null || levelId == null || levelId == ElementId.InvalidElementId)
                return new List<Element>();

            var filter = new ElementLevelFilter(levelId);

            return new FilteredElementCollector(doc)
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .ToList();
        }

        /// <summary>
        /// Get all levels in the document, sorted by elevation.
        /// </summary>
        public static IList<Level> GetLevels(Document doc)
        {
            if (doc == null) return new List<Level>();

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        #endregion

        #region Selected Elements

        /// <summary>
        /// Get elements from a collection of ElementIds.
        /// </summary>
        public static IList<Element> FromIds(Document doc, ICollection<ElementId> ids)
        {
            if (doc == null || ids == null || ids.Count == 0)
                return new List<Element>();

            return new FilteredElementCollector(doc, ids)
                .WhereElementIsNotElementType()
                .ToList();
        }

        /// <summary>
        /// Get elements of a specific type from a collection of ElementIds.
        /// </summary>
        public static IList<T> FromIds<T>(Document doc, ICollection<ElementId> ids) where T : Element
        {
            if (doc == null || ids == null || ids.Count == 0)
                return new List<T>();

            return new FilteredElementCollector(doc, ids)
                .OfClass(typeof(T))
                .Cast<T>()
                .ToList();
        }

        #endregion
    }
}
