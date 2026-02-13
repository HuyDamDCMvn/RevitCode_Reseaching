using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Service for collecting elements and their view-space bounding boxes.
    /// </summary>
    public class ElementCollector
    {
        private readonly Document _doc;
        private readonly View _view;
        private readonly TagTextFormatter _tagFormatter;

        public ElementCollector(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _tagFormatter = new TagTextFormatter(doc);
        }

        /// <summary>
        /// Get all taggable elements in the view for specified categories.
        /// </summary>
        public List<TaggableElement> GetTaggableElements(IEnumerable<BuiltInCategory> categories)
        {
            var result = new List<TaggableElement>();
            var categoryFilter = CreateCategoryFilter(categories);
            
            if (categoryFilter == null) return result;

            var collector = new FilteredElementCollector(_doc, _view.Id)
                .WherePasses(categoryFilter)
                .WhereElementIsNotElementType();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                var viewBounds = GetViewBounds(elem);
                if (viewBounds == null) continue;

                // Check for existing tags
                var (hasTag, tagId) = CheckExistingTag(elem);

                // Extract MEP system info
                var systemInfo = _tagFormatter.ExtractSystemInfo(elem);
                
                // Get linear element info
                var (isLinear, length, angle, startPt, endPt) = GetLinearElementInfo(elem);
                
                // Check if in group
                var (isInGroup, groupId) = CheckGroupMembership(elem);

                var taggable = new TaggableElement
                {
                    ElementId = elem.Id.Value,
                    CategoryName = elem.Category.Name,
                    FamilyName = GetFamilyName(elem),
                    TypeName = GetTypeName(elem),
                    ViewBounds = viewBounds.Value,
                    Center = viewBounds.Value.Center,
                    HasExistingTag = hasTag,
                    ExistingTagId = tagId,
                    // MEP System Info
                    BuiltInCategoryName = systemInfo.BuiltInCategory.ToString(),
                    SystemName = systemInfo.SystemName,
                    SystemClassification = systemInfo.SystemClassification,
                    SizeMM = systemInfo.SizeMM,
                    SizeString = systemInfo.SizeString,
                    ElevationM = systemInfo.ElevationM,
                    Slope = systemInfo.Slope,
                    // Linear element info
                    IsLinearElement = isLinear,
                    LengthFeet = length,
                    AngleRadians = angle,
                    StartPoint = startPt,
                    EndPoint = endPt,
                    // Group info
                    IsInGroup = isInGroup,
                    GroupId = groupId
                };

                result.Add(taggable);
            }

            return result;
        }

        /// <summary>
        /// Get taggable elements with pre-generated tag text using rule engine.
        /// </summary>
        public List<TaggableElement> GetTaggableElementsWithText(IEnumerable<BuiltInCategory> categories)
        {
            var elements = GetTaggableElements(categories);
            
            // Initialize rule engine
            var ruleEngine = RuleEngine.Instance;
            ruleEngine.Initialize();

            var viewType = GetViewType();

            foreach (var elem in elements)
            {
                // Get best matching rule
                var rule = ruleEngine.GetBestTaggingRule(
                    elem.BuiltInCategoryName ?? elem.CategoryName,
                    elem.FamilyName,
                    viewType,
                    elem.SystemClassification,
                    elem.SystemName);

                if (rule != null)
                {
                    elem.MatchedRuleId = rule.Id;
                    
                    // Generate tag text using rule pattern
                    var pattern = ruleEngine.GetTagPattern(rule, "pipeTag") 
                               ?? ruleEngine.GetTagPattern(rule, "pattern");
                    
                    if (pattern != null)
                    {
                        var info = new MepSystemInfo
                        {
                            SystemName = elem.SystemName,
                            SystemClassification = elem.SystemClassification,
                            SizeMM = elem.SizeMM,
                            SizeString = elem.SizeString,
                            ElevationM = elem.ElevationM,
                            FamilyName = elem.FamilyName,
                            TypeName = elem.TypeName
                        };
                        elem.GeneratedTagText = _tagFormatter.FormatTagText(pattern, info);
                    }
                }
            }

            return elements;
        }

        private string GetViewType()
        {
            return _view.ViewType switch
            {
                ViewType.FloorPlan => "FloorPlan",
                ViewType.CeilingPlan => "CeilingPlan",
                ViewType.Section => "Section",
                ViewType.Elevation => "Elevation",
                ViewType.ThreeD => "3D",
                _ => _view.ViewType.ToString()
            };
        }

        /// <summary>
        /// Get all existing tags in the view.
        /// </summary>
        public List<(long TagId, long HostId, BoundingBox2D Bounds)> GetExistingTags()
        {
            var result = new List<(long, long, BoundingBox2D)>();

            var collector = new FilteredElementCollector(_doc, _view.Id)
                .OfClass(typeof(IndependentTag))
                .WhereElementIsNotElementType();

            foreach (IndependentTag tag in collector)
            {
                // Get tagged element IDs - API changed in Revit 2025
                var taggedIds = tag.GetTaggedLocalElementIds();
                if (taggedIds == null || taggedIds.Count == 0) continue;

                var hostId = taggedIds.First();
                var viewBounds = GetViewBounds(tag);
                if (viewBounds == null) continue;

                result.Add((tag.Id.Value, hostId.Value, viewBounds.Value));
            }

            return result;
        }
        
        /// <summary>
        /// Get bounds of all annotations (text notes, dimensions) in the view.
        /// Used for collision avoidance.
        /// </summary>
        public List<BoundingBox2D> GetAnnotationBounds()
        {
            var result = new List<BoundingBox2D>();
            
            // Collect TextNotes
            try
            {
                var textCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(TextNote))
                    .WhereElementIsNotElementType();
                
                foreach (var text in textCollector)
                {
                    var bounds = GetViewBounds(text);
                    if (bounds.HasValue) result.Add(bounds.Value);
                }
            }
            catch { /* TextNote collection failed */ }
            
            // Collect Dimensions
            try
            {
                var dimCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(Dimension))
                    .WhereElementIsNotElementType();
                
                foreach (var dim in dimCollector)
                {
                    var bounds = GetViewBounds(dim);
                    if (bounds.HasValue) result.Add(bounds.Value);
                }
            }
            catch { /* Dimension collection failed */ }
            
            // Collect SpotDimensions
            try
            {
                var spotCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfCategory(BuiltInCategory.OST_SpotElevations)
                    .WhereElementIsNotElementType();
                
                foreach (var spot in spotCollector)
                {
                    var bounds = GetViewBounds(spot);
                    if (bounds.HasValue) result.Add(bounds.Value);
                }
            }
            catch { /* SpotDimension collection failed */ }
            
            // Collect Grid lines/bubbles (they can overlap with tags)
            try
            {
                var gridCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(Grid))
                    .WhereElementIsNotElementType();
                
                foreach (Grid grid in gridCollector)
                {
                    // Get grid bubble positions
                    var curve = grid.Curve;
                    if (curve != null)
                    {
                        // Add small bounds at each endpoint (bubble locations)
                        const double bubbleSize = 0.5; // feet
                        var startPt = curve.GetEndPoint(0);
                        var endPt = curve.GetEndPoint(1);
                        
                        var startBounds = GetViewBoundsForPoint(startPt, bubbleSize);
                        var endBounds = GetViewBoundsForPoint(endPt, bubbleSize);
                        
                        if (startBounds.HasValue) result.Add(startBounds.Value);
                        if (endBounds.HasValue) result.Add(endBounds.Value);
                    }
                }
            }
            catch { /* Grid collection failed */ }
            
            return result;
        }
        
        /// <summary>
        /// Get view bounds for a point with specified size.
        /// </summary>
        private BoundingBox2D? GetViewBoundsForPoint(XYZ point, double size)
        {
            try
            {
                var rightDir = _view.RightDirection;
                var upDir = _view.UpDirection;
                var origin = _view.Origin;
                
                var relative = point - origin;
                var x = relative.DotProduct(rightDir);
                var y = relative.DotProduct(upDir);
                
                return new BoundingBox2D(
                    x - size, y - size,
                    x + size, y + size);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get available tag types for a category.
        /// </summary>
        public List<(long TypeId, string Name)> GetTagTypesForCategory(BuiltInCategory category)
        {
            var result = new List<(long, string)>();

            var collector = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Tags)
                .WhereElementIsElementType();

            foreach (FamilySymbol symbol in collector)
            {
                // Check if this tag can tag the specified category
                var family = symbol.Family;
                if (family == null) continue;

                // Get the category that this tag family can tag
                var taggedCategory = GetTaggedCategory(family);
                if (taggedCategory == category)
                {
                    result.Add((symbol.Id.Value, $"{family.Name}: {symbol.Name}"));
                }
            }

            return result;
        }

        /// <summary>
        /// Get category statistics for the view.
        /// </summary>
        public List<CategoryTagConfig> GetCategoryStats()
        {
            var stats = new Dictionary<BuiltInCategory, CategoryTagConfig>();

            // Collect elements
            var collector = new FilteredElementCollector(_doc, _view.Id)
                .WhereElementIsNotElementType();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;

                var catId = elem.Category.Id;
                BuiltInCategory builtInCat;
                
                try
                {
                    builtInCat = (BuiltInCategory)catId.Value;
                }
                catch
                {
                    continue;
                }

                // Only include model categories that can be tagged
                if (!IsTaggableCategory(builtInCat)) continue;

                if (!stats.TryGetValue(builtInCat, out var config))
                {
                    config = new CategoryTagConfig
                    {
                        Category = builtInCat,
                        DisplayName = elem.Category.Name,
                        IsSelected = false,
                        ElementCount = 0,
                        TaggedCount = 0
                    };
                    stats[builtInCat] = config;
                }

                config.ElementCount++;

                // Check if tagged
                var (hasTag, _) = CheckExistingTag(elem);
                if (hasTag) config.TaggedCount++;
            }

            return stats.Values
                .OrderBy(c => c.DisplayName)
                .ToList();
        }

        private ElementFilter CreateCategoryFilter(IEnumerable<BuiltInCategory> categories)
        {
            var catList = categories.ToList();
            if (catList.Count == 0) return null;
            if (catList.Count == 1) return new ElementCategoryFilter(catList[0]);

            var filters = catList.Select(c => new ElementCategoryFilter(c)).ToList();
            return new LogicalOrFilter(filters.Cast<ElementFilter>().ToList());
        }

        private BoundingBox2D? GetViewBounds(Element elem)
        {
            var bb = elem.get_BoundingBox(_view);
            if (bb == null) return null;

            // Transform to view coordinates
            var viewDir = _view.ViewDirection;
            var rightDir = _view.RightDirection;
            var upDir = _view.UpDirection;
            var origin = _view.Origin;

            // Project min/max to view plane
            var points = new[]
            {
                bb.Min,
                bb.Max,
                new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z),
                new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)
            };

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var pt in points)
            {
                var relative = pt - origin;
                var x = relative.DotProduct(rightDir);
                var y = relative.DotProduct(upDir);

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            return new BoundingBox2D(minX, minY, maxX, maxY);
        }

        private (bool HasTag, long? TagId) CheckExistingTag(Element elem)
        {
            try
            {
                var tagCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(IndependentTag))
                    .WhereElementIsNotElementType();

                foreach (IndependentTag tag in tagCollector)
                {
                    // Get tagged element IDs - API changed in Revit 2025
                    var taggedIds = tag.GetTaggedLocalElementIds();
                    if (taggedIds != null && taggedIds.Contains(elem.Id))
                    {
                        return (true, tag.Id.Value);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return (false, null);
        }

        private string GetFamilyName(Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol?.Family != null)
            {
                return fi.Symbol.Family.Name;
            }

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = _doc.GetElement(typeId);
                if (elemType != null)
                {
                    var familyParam = elemType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    if (familyParam != null && familyParam.HasValue)
                    {
                        return familyParam.AsString();
                    }
                }
            }

            return elem.GetType().Name;
        }

        private string GetTypeName(Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol != null)
            {
                return fi.Symbol.Name;
            }

            var typeId = elem.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var elemType = _doc.GetElement(typeId);
                if (elemType != null)
                {
                    return elemType.Name;
                }
            }

            return elem.Name ?? "-";
        }

        private BuiltInCategory GetTaggedCategory(Family tagFamily)
        {
            // Try to get from family parameter
            var param = tagFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
            if (param != null && param.HasValue)
            {
                return (BuiltInCategory)param.AsInteger();
            }

            // Fallback: check family name for hints
            var name = tagFamily.Name.ToLower();
            if (name.Contains("wall")) return BuiltInCategory.OST_Walls;
            if (name.Contains("door")) return BuiltInCategory.OST_Doors;
            if (name.Contains("window")) return BuiltInCategory.OST_Windows;
            if (name.Contains("room")) return BuiltInCategory.OST_Rooms;
            if (name.Contains("duct")) return BuiltInCategory.OST_DuctCurves;
            if (name.Contains("pipe")) return BuiltInCategory.OST_PipeCurves;

            return BuiltInCategory.INVALID;
        }

        /// <summary>
        /// Get linear element information (for pipes, ducts, cable trays).
        /// </summary>
        private (bool IsLinear, double Length, double Angle, Point2D? Start, Point2D? End) GetLinearElementInfo(Element elem)
        {
            try
            {
                if (elem == null) return (false, 0, 0, null, null);
                
                // Check if element has a curve (MEPCurve or similar)
                LocationCurve locCurve = elem.Location as LocationCurve;
                if (locCurve?.Curve == null)
                {
                    return (false, 0, 0, null, null);
                }
                
                var curve = locCurve.Curve;
                if (curve == null) return (false, 0, 0, null, null);
                
                var length = curve.Length;
                
                // Validate length
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length))
                {
                    return (false, 0, 0, null, null);
                }
                
                // Get endpoints
                XYZ startPt3D, endPt3D;
                try
                {
                    startPt3D = curve.GetEndPoint(0);
                    endPt3D = curve.GetEndPoint(1);
                }
                catch
                {
                    return (false, 0, 0, null, null);
                }
                
                if (startPt3D == null || endPt3D == null)
                    return (false, 0, 0, null, null);
                
                // Transform to view coordinates
                var rightDir = _view.RightDirection;
                var upDir = _view.UpDirection;
                var origin = _view.Origin;
                
                if (rightDir == null || upDir == null || origin == null)
                    return (false, 0, 0, null, null);
                
                var startRelative = startPt3D - origin;
                var startPt = new Point2D(
                    startRelative.DotProduct(rightDir),
                    startRelative.DotProduct(upDir));
                
                var endRelative = endPt3D - origin;
                var endPt = new Point2D(
                    endRelative.DotProduct(rightDir),
                    endRelative.DotProduct(upDir));
                
                // Validate coordinates
                if (double.IsNaN(startPt.X) || double.IsNaN(startPt.Y) ||
                    double.IsNaN(endPt.X) || double.IsNaN(endPt.Y))
                {
                    return (false, 0, 0, null, null);
                }
                
                // Calculate angle in view coordinates
                var dx = endPt.X - startPt.X;
                var dy = endPt.Y - startPt.Y;
                var angle = Math.Atan2(dy, dx);
                
                // Validate angle
                if (double.IsNaN(angle)) angle = 0;
                
                return (true, length, angle, startPt, endPt);
            }
            catch
            {
                return (false, 0, 0, null, null);
            }
        }
        
        /// <summary>
        /// Check if element is in a group.
        /// </summary>
        private (bool IsInGroup, long? GroupId) CheckGroupMembership(Element elem)
        {
            try
            {
                var groupId = elem.GroupId;
                if (groupId != null && groupId != ElementId.InvalidElementId)
                {
                    return (true, groupId.Value);
                }
            }
            catch
            {
                // GroupId access failed
            }
            return (false, null);
        }

        private bool IsTaggableCategory(BuiltInCategory cat)
        {
            // Common taggable categories
            var taggable = new HashSet<BuiltInCategory>
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Ceilings,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_StructuralFoundation,
                BuiltInCategory.OST_Rooms,
                BuiltInCategory.OST_Areas,
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_Sprinklers,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_SpecialityEquipment,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_FlexPipeCurves,
                BuiltInCategory.OST_CableTrayFitting,
                BuiltInCategory.OST_ConduitFitting
            };

            return taggable.Contains(cat);
        }
    }
}
