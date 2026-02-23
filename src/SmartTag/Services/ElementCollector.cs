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
    /// OPTIMIZED: Caches existing tags to avoid repeated FilteredElementCollector calls.
    /// </summary>
    public class ElementCollector
    {
        private readonly Document _doc;
        private readonly View _view;
        private readonly TagTextFormatter _tagFormatter;
        
        // CACHE: Existing tags - loaded once, used many times
        private Dictionary<long, long> _taggedElementCache; // ElementId -> TagId
        private HashSet<long> _emptyTagCache;
        private bool _tagCacheInitialized;

        public ElementCollector(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _tagFormatter = new TagTextFormatter(doc);
            _taggedElementCache = new Dictionary<long, long>();
            _emptyTagCache = new HashSet<long>();
            _tagCacheInitialized = false;
        }
        
        /// <summary>
        /// Initialize tag cache once - O(n) instead of O(n*m)
        /// </summary>
        private void EnsureTagCacheInitialized()
        {
            if (_tagCacheInitialized) return;
            
            try
            {
                var tagCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(IndependentTag))
                    .WhereElementIsNotElementType();
                
                foreach (IndependentTag tag in tagCollector)
                {
                    try
                    {
                        var taggedIds = GetTaggedElementIds(tag);
                        if (taggedIds != null)
                        {
                            bool hasText = true;
                            try { hasText = tag.HasTagText(); } catch { }
                            foreach (var id in taggedIds)
                            {
                                if (id != ElementId.InvalidElementId)
                                {
                                    _taggedElementCache[id.Value] = tag.Id.Value;
                                    if (!hasText)
                                        _emptyTagCache.Add(id.Value);
                                }
                            }
                        }
                    }
                    catch { /* Ignore individual tag errors */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tag cache initialization error: {ex.Message}");
            }
            
            _tagCacheInitialized = true;
            System.Diagnostics.Debug.WriteLine($"Tag cache initialized: {_taggedElementCache.Count} tagged elements");
        }

        /// <summary>
        /// Get tagged element ID(s) from IndependentTag. Compatible with Revit 2024 (TaggedLocalElementId) and 2025+ (GetTaggedLocalElementIds).
        /// </summary>
        private static IList<ElementId> GetTaggedElementIds(IndependentTag tag)
        {
            if (tag == null) return null;
            try
            {
                var taggedIds = tag.GetTaggedLocalElementIds();
                if (taggedIds != null && taggedIds.Count > 0)
                    return taggedIds.ToList();
            }
            catch
            {
                // Revit 2024-: use TaggedLocalElementId property
                try
                {
                    var prop = typeof(IndependentTag).GetProperty("TaggedLocalElementId");
                    if (prop?.GetValue(tag) is ElementId singleId && singleId != null && singleId != ElementId.InvalidElementId)
                        return new[] { singleId };
                }
                catch { }
            }
            return null;
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
                var taggedIds = GetTaggedElementIds(tag);
                if (taggedIds == null || taggedIds.Count == 0) continue;

                var hostId = taggedIds[0];
                if (hostId == null || hostId == ElementId.InvalidElementId) continue;

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

            // Collect ClearanceZone elements (e.g. ClearanceZone_Rectangular) - tags must not overlap these
            const double clearanceMargin = 0.5; // feet - buffer so tags don't sit on zone edge
            try
            {
                var fiCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType();
                foreach (FamilyInstance fi in fiCollector)
                {
                    try
                    {
                        var familyName = fi.Symbol?.Family?.Name ?? "";
                        if (string.IsNullOrEmpty(familyName) || familyName.IndexOf("ClearanceZone", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var bounds = GetViewBounds(fi);
                        if (bounds.HasValue)
                        {
                            var b = bounds.Value;
                            result.Add(new BoundingBox2D(
                                b.MinX - clearanceMargin, b.MinY - clearanceMargin,
                                b.MaxX + clearanceMargin, b.MaxY + clearanceMargin));
                        }
                    }
                    catch { /* skip single element */ }
                }
            }
            catch { /* ClearanceZone collection failed */ }
            
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
        public List<TagTypeInfo> GetTagTypesForCategory(BuiltInCategory category)
        {
            var result = new List<TagTypeInfo>();
            var tagCategory = GetTagCategoryForElementCategory(category);
            
            if (tagCategory == BuiltInCategory.INVALID) 
            {
                // Fallback: try generic annotation
                tagCategory = BuiltInCategory.OST_GenericAnnotation;
            }

            var collector = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(tagCategory)
                .WhereElementIsElementType();

            foreach (FamilySymbol symbol in collector)
            {
                var family = symbol.Family;
                result.Add(new TagTypeInfo
                {
                    TypeId = symbol.Id.Value,
                    FamilyName = family?.Name ?? "Unknown",
                    TypeName = symbol.Name
                });
            }

            // Sort by Family then Type
            return result.OrderBy(t => t.FamilyName).ThenBy(t => t.TypeName).ToList();
        }
        
        /// <summary>
        /// Map element category to its corresponding tag category.
        /// </summary>
        private BuiltInCategory GetTagCategoryForElementCategory(BuiltInCategory elementCategory)
        {
            var mappings = new Dictionary<BuiltInCategory, BuiltInCategory>
            {
                { BuiltInCategory.OST_Walls, BuiltInCategory.OST_WallTags },
                { BuiltInCategory.OST_Doors, BuiltInCategory.OST_DoorTags },
                { BuiltInCategory.OST_Windows, BuiltInCategory.OST_WindowTags },
                { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_RoomTags },
                { BuiltInCategory.OST_Areas, BuiltInCategory.OST_AreaTags },
                { BuiltInCategory.OST_Floors, BuiltInCategory.OST_FloorTags },
                { BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_CeilingTags },
                { BuiltInCategory.OST_Roofs, BuiltInCategory.OST_RoofTags },
                { BuiltInCategory.OST_Columns, BuiltInCategory.OST_ColumnTags },
                { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralColumnTags },
                { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingTags },
                { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralFoundationTags },
                { BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureTags },
                { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_GenericModelTags },
                { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags },
                { BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalEquipmentTags },
                { BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalFixtureTags },
                { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingFixtureTags },
                { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PlumbingFixtureTags },
                { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctTags },
                { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeTags },
                { BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctAccessoryTags },
                { BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctFittingTags },
                { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctTerminalTags },
                { BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PipeAccessoryTags },
                { BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeFittingTags },
                { BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_SprinklerTags },
                { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayTags },
                { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitTags },
                { BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_SpecialityEquipmentTags }
            };

            return mappings.TryGetValue(elementCategory, out var tagCat) ? tagCat : BuiltInCategory.INVALID;
        }

        /// <summary>
        /// Get category statistics for the view.
        /// OPTIMIZED: Uses category filter and cached tag lookup.
        /// </summary>
        public List<CategoryTagConfig> GetCategoryStats()
        {
            var stats = new Dictionary<BuiltInCategory, CategoryTagConfig>();
            
            // Initialize tag cache FIRST (only once)
            EnsureTagCacheInitialized();

            // Filter for common taggable categories to reduce iteration
            var taggableCategories = new[]
            {
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_FlexPipeCurves,
                BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_PipeFitting,
                BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_PipeAccessory,
                BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_Sprinklers,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_CableTrayFitting,
                BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_ConduitFitting,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_FireAlarmDevices,
                BuiltInCategory.OST_DataDevices,
                BuiltInCategory.OST_CommunicationDevices,
                BuiltInCategory.OST_SecurityDevices,
                BuiltInCategory.OST_DuctTerminal,
                BuiltInCategory.OST_GenericModel
            };
            
            // Create multi-category filter for faster collection
            var categoryFilters = taggableCategories
                .Select(c => new ElementCategoryFilter(c))
                .Cast<ElementFilter>()
                .ToList();
            
            var multiCatFilter = new LogicalOrFilter(categoryFilters);
            
            // Collect elements with category filter
            var collector = new FilteredElementCollector(_doc, _view.Id)
                .WherePasses(multiCatFilter)
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

                // O(1) lookup from cache - FAST!
                if (_taggedElementCache.ContainsKey(elem.Id.Value))
                {
                    config.TaggedCount++;
                }
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

        /// <summary>
        /// Check if element has existing tag - OPTIMIZED using cache
        /// </summary>
        private (bool HasTag, long? TagId) CheckExistingTag(Element elem)
        {
            EnsureTagCacheInitialized();
            if (_taggedElementCache.TryGetValue(elem.Id.Value, out var tagId))
                return (true, tagId);
            return (false, null);
        }

        public bool HasEmptyTag(long elementId)
        {
            EnsureTagCacheInitialized();
            return _emptyTagCache.Contains(elementId);
        }

        public int EmptyTagCount
        {
            get
            {
                EnsureTagCacheInitialized();
                return _emptyTagCache?.Count ?? 0;
            }
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

    }
}
