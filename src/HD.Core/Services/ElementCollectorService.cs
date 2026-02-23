using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using HD.Core.Models;

namespace HD.Core.Services
{
    /// <summary>
    /// Service for collecting and filtering elements from Revit documents.
    /// Follows early-filtering pattern for performance.
    /// </summary>
    public static class ElementCollectorService
    {
        #region By Class

        public static IList<T> OfClass<T>(Document doc) where T : Element
        {
            if (doc == null) return new List<T>();
            return new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .ToList();
        }

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

        public static IList<Element> OfCategory(Document doc, BuiltInCategory category)
        {
            if (doc == null) return new List<Element>();
            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();
        }

        public static IList<Element> OfCategory(Document doc, ElementId viewId, BuiltInCategory category)
        {
            if (doc == null || viewId == null || viewId == ElementId.InvalidElementId)
                return new List<Element>();
            return new FilteredElementCollector(doc, viewId)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToList();
        }

        #endregion

        #region Visible Elements in View

        public static IList<Element> GetVisibleElements(Document doc, ElementId viewId)
        {
            if (doc == null || viewId == null || viewId == ElementId.InvalidElementId)
                return new List<Element>();

            // Use ElementIsElementTypeFilter to push type-exclusion into Revit's native filter
            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();

            var result = new List<Element>();
            foreach (var e in collector)
            {
                if (e.Category != null && e.Category.CategoryType == CategoryType.Model)
                    result.Add(e);
            }
            return result;
        }

        #endregion

        #region Categories for Isolate

        public static List<CategoryItem> GetCategoriesForIsolate(Document doc, ElementId viewId)
        {
            var result = new Dictionary<string, CategoryItem>(StringComparer.Ordinal);

            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                var catName = elem.Category.Name;
                if (string.IsNullOrEmpty(catName)) continue;

                if (!result.TryGetValue(catName, out var item))
                {
                    item = new CategoryItem
                    {
                        Name = catName,
                        CategoryId = elem.Category.Id.Value,
                        ElementCount = 0,
                        ElementIds = new List<long>(64)
                    };
                    result[catName] = item;
                }

                item.ElementCount++;
                item.ElementIds.Add(elem.Id.Value);
            }

            return result.Values.OrderBy(c => c.Name).ToList();
        }

        #endregion

        #region Families for Category

        public static List<FamilyItem> GetFamiliesForCategory(Document doc, ElementId viewId, string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)) return new List<FamilyItem>();

            var result = new Dictionary<string, FamilyItem>(StringComparer.Ordinal);

            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            try
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat?.Name == categoryName)
                    {
                        collector = collector.OfCategoryId(cat.Id);
                        break;
                    }
                }
            }
            catch { /* Fall back to no category filter */ }

            foreach (var elem in collector)
            {
                if (elem.Category == null || elem.Category.Name != categoryName) continue;

                var familyName = GetFamilyName(doc, elem);
                if (string.IsNullOrEmpty(familyName)) continue;

                if (!result.TryGetValue(familyName, out var item))
                {
                    item = new FamilyItem
                    {
                        FamilyName = familyName,
                        CategoryName = categoryName,
                        ElementCount = 0,
                        ElementIds = new List<long>(32)
                    };
                    result[familyName] = item;
                }

                item.ElementCount++;
                item.ElementIds.Add(elem.Id.Value);
            }

            return result.Values.OrderBy(f => f.FamilyName).ToList();
        }

        #endregion

        #region Family Types

        public static List<FamilyTypeItem> GetFamilyTypesForFamily(Document doc, ElementId viewId, string categoryName, string familyName)
        {
            if (string.IsNullOrEmpty(familyName)) return new List<FamilyTypeItem>();

            var result = new Dictionary<string, FamilyTypeItem>(StringComparer.Ordinal);

            var collector = new FilteredElementCollector(doc, viewId)
                .WhereElementIsNotElementType();
            if (!string.IsNullOrEmpty(categoryName))
            {
                try
                {
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat?.Name == categoryName)
                        {
                            collector = collector.OfCategoryId(cat.Id);
                            break;
                        }
                    }
                }
                catch { /* Fall back to no category filter */ }
            }

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                if (!string.IsNullOrEmpty(categoryName) && elem.Category.Name != categoryName) continue;

                var elemFamilyName = GetFamilyName(doc, elem);
                if (elemFamilyName != familyName) continue;

                var typeName = GetTypeName(doc, elem);
                if (string.IsNullOrEmpty(typeName)) continue;

                if (!result.TryGetValue(typeName, out var item))
                {
                    item = new FamilyTypeItem
                    {
                        TypeName = typeName,
                        FamilyName = familyName,
                        CategoryName = categoryName,
                        ElementCount = 0,
                        ElementIds = new List<long>(16)
                    };
                    result[typeName] = item;
                }

                item.ElementCount++;
                item.ElementIds.Add(elem.Id.Value);
            }

            return result.Values.OrderBy(t => t.TypeName).ToList();
        }

        #endregion

        #region Helper Methods

        public static string GetFamilyName(Document doc, Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol?.Family != null)
                return fi.Symbol.Family.Name;

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    var famNameParam = elemType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (famNameParam?.HasValue == true)
                        return famNameParam.AsString();
                    famNameParam = elemType.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME);
                    if (famNameParam?.HasValue == true)
                        return famNameParam.AsString();
                    return elemType.Name;
                }
            }

            return elem.Name ?? elem.Category?.Name ?? "Unknown";
        }

        public static string GetTypeName(Document doc, Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol != null)
                return fi.Symbol.Name;

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = doc.GetElement(typeId);
                if (elemType != null)
                {
                    var typeNameParam = elemType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME);
                    if (typeNameParam?.HasValue == true)
                        return typeNameParam.AsString();
                    return elemType.Name;
                }
            }

            return elem.Name ?? "Unknown";
        }

        #endregion
    }
}
