using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace HDRevitLib
{
    /// <summary>
    /// Interface for selection filters used with PickElement utilities.
    /// </summary>
    public interface ISelectionFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
    {
    }

    /// <summary>
    /// Selection filter that accepts elements of specific categories.
    /// </summary>
    public class CategorySelectionFilter : ISelectionFilter
    {
        private readonly HashSet<BuiltInCategory> _categories;

        public CategorySelectionFilter(params BuiltInCategory[] categories)
        {
            _categories = new HashSet<BuiltInCategory>(categories);
        }

        public CategorySelectionFilter(IEnumerable<BuiltInCategory> categories)
        {
            _categories = new HashSet<BuiltInCategory>(categories);
        }

        public bool AllowElement(Element elem)
        {
            if (elem?.Category == null) return false;

            var catId = elem.Category.Id.IntegerValue;
            return _categories.Contains((BuiltInCategory)catId);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }

    /// <summary>
    /// Selection filter that accepts elements of a specific class type.
    /// </summary>
    public class ClassSelectionFilter<T> : ISelectionFilter where T : Element
    {
        public bool AllowElement(Element elem)
        {
            return elem is T;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }

    /// <summary>
    /// Selection filter that accepts only Wall elements.
    /// </summary>
    public class WallSelectionFilter : ClassSelectionFilter<Wall>
    {
    }

    /// <summary>
    /// Selection filter that accepts only Floor elements.
    /// </summary>
    public class FloorSelectionFilter : ClassSelectionFilter<Floor>
    {
    }

    /// <summary>
    /// Selection filter that accepts only FamilyInstance elements.
    /// </summary>
    public class FamilyInstanceSelectionFilter : ClassSelectionFilter<FamilyInstance>
    {
    }

    /// <summary>
    /// Selection filter that accepts elements based on a custom predicate.
    /// </summary>
    public class PredicateSelectionFilter : ISelectionFilter
    {
        private readonly System.Func<Element, bool> _predicate;

        public PredicateSelectionFilter(System.Func<Element, bool> predicate)
        {
            _predicate = predicate;
        }

        public bool AllowElement(Element elem)
        {
            if (elem == null || _predicate == null) return false;
            return _predicate(elem);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }

    /// <summary>
    /// Selection filter for picking faces of elements.
    /// </summary>
    public class FaceSelectionFilter : ISelectionFilter
    {
        private readonly HashSet<BuiltInCategory> _categories;

        public FaceSelectionFilter()
        {
            _categories = null;
        }

        public FaceSelectionFilter(params BuiltInCategory[] categories)
        {
            _categories = new HashSet<BuiltInCategory>(categories);
        }

        public bool AllowElement(Element elem)
        {
            if (elem == null) return false;
            if (_categories == null) return true;
            if (elem.Category == null) return false;

            var catId = elem.Category.Id.IntegerValue;
            return _categories.Contains((BuiltInCategory)catId);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return reference?.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_SURFACE;
        }
    }

    /// <summary>
    /// Selection filter for picking edges of elements.
    /// </summary>
    public class EdgeSelectionFilter : ISelectionFilter
    {
        private readonly HashSet<BuiltInCategory> _categories;

        public EdgeSelectionFilter()
        {
            _categories = null;
        }

        public EdgeSelectionFilter(params BuiltInCategory[] categories)
        {
            _categories = new HashSet<BuiltInCategory>(categories);
        }

        public bool AllowElement(Element elem)
        {
            if (elem == null) return false;
            if (_categories == null) return true;
            if (elem.Category == null) return false;

            var catId = elem.Category.Id.IntegerValue;
            return _categories.Contains((BuiltInCategory)catId);
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return reference?.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_LINEAR;
        }
    }

    /// <summary>
    /// Selection filter that combines multiple filters with AND logic.
    /// </summary>
    public class AndSelectionFilter : ISelectionFilter
    {
        private readonly ISelectionFilter[] _filters;

        public AndSelectionFilter(params ISelectionFilter[] filters)
        {
            _filters = filters ?? new ISelectionFilter[0];
        }

        public bool AllowElement(Element elem)
        {
            foreach (var filter in _filters)
            {
                if (!filter.AllowElement(elem)) return false;
            }
            return true;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            foreach (var filter in _filters)
            {
                if (!filter.AllowReference(reference, position)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Selection filter that combines multiple filters with OR logic.
    /// </summary>
    public class OrSelectionFilter : ISelectionFilter
    {
        private readonly ISelectionFilter[] _filters;

        public OrSelectionFilter(params ISelectionFilter[] filters)
        {
            _filters = filters ?? new ISelectionFilter[0];
        }

        public bool AllowElement(Element elem)
        {
            if (_filters.Length == 0) return false;

            foreach (var filter in _filters)
            {
                if (filter.AllowElement(elem)) return true;
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            if (_filters.Length == 0) return false;

            foreach (var filter in _filters)
            {
                if (filter.AllowReference(reference, position)) return true;
            }
            return false;
        }
    }
}
