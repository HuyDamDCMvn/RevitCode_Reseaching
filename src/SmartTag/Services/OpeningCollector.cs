using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Collects and categorizes openings for dimensioning.
    /// </summary>
    public class OpeningCollector
    {
        private readonly Document _doc;
        private readonly View _view;

        public OpeningCollector(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <summary>
        /// Collects all dimensionable openings visible in the current view.
        /// </summary>
        public List<DimensionableOpening> CollectOpenings(DimensionSettings settings)
        {
            var result = new List<DimensionableOpening>();

            foreach (var category in settings.OpeningCategories)
            {
                try
                {
                    var collector = new FilteredElementCollector(_doc, _view.Id)
                        .OfCategory(category)
                        .WhereElementIsNotElementType();

                    foreach (Element elem in collector)
                    {
                        var opening = CreateDimensionableOpening(elem);
                        if (opening != null)
                        {
                            result.Add(opening);
                        }
                    }
                }
                catch
                {
                    // Skip invalid categories
                }
            }

            // Also collect Generic Models that might be openings (family name contains "Opening")
            try
            {
                var genericCollector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_GenericModel)
                    .WhereElementIsNotElementType();

                foreach (FamilyInstance fi in genericCollector)
                {
                    var familyName = fi.Symbol?.Family?.Name ?? "";
                    if (familyName.Contains("Opening", StringComparison.OrdinalIgnoreCase) ||
                        familyName.Contains("Penetration", StringComparison.OrdinalIgnoreCase) ||
                        familyName.Contains("Durchbruch", StringComparison.OrdinalIgnoreCase) || // German
                        familyName.Contains("BDB", StringComparison.OrdinalIgnoreCase) ||
                        familyName.Contains("WDB", StringComparison.OrdinalIgnoreCase))
                    {
                        var opening = CreateDimensionableOpening(fi);
                        if (opening != null && !result.Any(o => o.ElementId == opening.ElementId))
                        {
                            result.Add(opening);
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return result;
        }

        /// <summary>
        /// Creates a DimensionableOpening from a Revit element.
        /// </summary>
        private DimensionableOpening CreateDimensionableOpening(Element elem)
        {
            if (elem == null) return null;

            try
            {
                var bbox = elem.get_BoundingBox(_view);
                if (bbox == null) return null;

                var center = (bbox.Min + bbox.Max) / 2.0;
                var width = bbox.Max.X - bbox.Min.X;
                var height = bbox.Max.Y - bbox.Min.Y;
                var depth = bbox.Max.Z - bbox.Min.Z;

                var opening = new DimensionableOpening
                {
                    ElementId = elem.Id.Value,
                    CenterPoint = center,
                    ViewCenter = new Point2D(center.X, center.Y),
                    Width = width,
                    Height = height,
                    BottomElevation = bbox.Min.Z
                };

                // Determine opening type from family name or parameters
                DetermineOpeningType(elem, opening);

                // Get references for dimensioning
                GetReferences(elem, opening);

                // Get system name from parameters
                opening.SystemName = GetParameterValue(elem, "SN") ??
                                    GetParameterValue(elem, "System Name") ??
                                    GetParameterValue(elem, "System") ?? "";

                return opening;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Determines the opening type based on family name and geometry.
        /// </summary>
        private void DetermineOpeningType(Element elem, DimensionableOpening opening)
        {
            var familyName = "";
            var typeName = "";

            if (elem is FamilyInstance fi)
            {
                familyName = fi.Symbol?.Family?.Name ?? "";
                typeName = fi.Symbol?.Name ?? "";
            }

            var combinedName = $"{familyName} {typeName}".ToUpperInvariant();

            // Determine if floor or wall opening
            opening.IsFloorOpening = combinedName.Contains("BDB") ||
                                     combinedName.Contains("FLOOR") ||
                                     combinedName.Contains("BODEN") ||
                                     elem.Category?.Id.Value == (long)BuiltInCategory.OST_FloorOpening;

            // Determine if circular
            opening.IsCircular = combinedName.Contains("_RU") ||
                                combinedName.Contains("ROUND") ||
                                combinedName.Contains("CIRCULAR") ||
                                combinedName.Contains("RUND");

            if (opening.IsCircular)
            {
                // For circular, use the smaller dimension as diameter
                opening.Diameter = Math.Min(opening.Width, opening.Height);
            }

            // Set type code
            var prefix = opening.IsFloorOpening ? "BDB" : "WDB";
            var shape = opening.IsCircular ? "RU" : "RE";
            opening.OpeningType = $"{prefix}_{shape}";
        }

        /// <summary>
        /// Gets dimensionable references from the element.
        /// </summary>
        private void GetReferences(Element elem, DimensionableOpening opening)
        {
            try
            {
                // Get geometry for references
                var opt = new Options
                {
                    View = _view,
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = false
                };

                var geom = elem.get_Geometry(opt);
                if (geom == null) return;

                // Try to get center reference from location
                if (elem.Location is LocationPoint lp)
                {
                    opening.CenterReference = new Reference(elem);
                }
                else if (elem.Location is LocationCurve lc)
                {
                    opening.CenterReference = new Reference(elem);
                }

                // If no location-based reference, use element reference
                if (opening.CenterReference == null)
                {
                    opening.CenterReference = new Reference(elem);
                }
            }
            catch
            {
                // Use element reference as fallback
                try
                {
                    opening.CenterReference = new Reference(elem);
                }
                catch
                {
                    // Ignore
                }
            }
        }

        /// <summary>
        /// Gets a parameter value from an element.
        /// </summary>
        private string GetParameterValue(Element elem, string paramName)
        {
            try
            {
                // Try instance parameter
                var param = elem.LookupParameter(paramName);
                if (param != null && param.HasValue)
                {
                    return param.AsValueString() ?? param.AsString();
                }

                // Try type parameter
                var typeId = elem.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var type = _doc.GetElement(typeId);
                    param = type?.LookupParameter(paramName);
                    if (param != null && param.HasValue)
                    {
                        return param.AsValueString() ?? param.AsString();
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        }

        /// <summary>
        /// Collects grid lines visible in the view.
        /// </summary>
        public List<DimensionReference> CollectGrids()
        {
            var result = new List<DimensionReference>();

            try
            {
                var collector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(Grid))
                    .WhereElementIsNotElementType();

                foreach (Grid grid in collector)
                {
                    var curve = grid.Curve;
                    if (curve is Line line)
                    {
                        var dimRef = new DimensionReference
                        {
                            ElementId = grid.Id.Value,
                            Name = grid.Name,
                            IsGrid = true,
                            IsWall = false,
                            ViewLine = line,
                            Reference = new Reference(grid)
                        };

                        // Calculate position for sorting
                        // For vertical grids (running N-S), use X coordinate
                        // For horizontal grids (running E-W), use Y coordinate
                        var dir = line.Direction.Normalize();
                        if (Math.Abs(dir.Y) > Math.Abs(dir.X))
                        {
                            // Vertical grid
                            dimRef.Position = line.GetEndPoint(0).X;
                        }
                        else
                        {
                            // Horizontal grid
                            dimRef.Position = line.GetEndPoint(0).Y;
                        }

                        result.Add(dimRef);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return result;
        }

        /// <summary>
        /// Collects structural walls visible in the view (for reference).
        /// </summary>
        public List<DimensionReference> CollectWalls()
        {
            var result = new List<DimensionReference>();

            try
            {
                var collector = new FilteredElementCollector(_doc, _view.Id)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType();

                foreach (Wall wall in collector)
                {
                    var curve = (wall.Location as LocationCurve)?.Curve;
                    if (curve is Line line)
                    {
                        var dimRef = new DimensionReference
                        {
                            ElementId = wall.Id.Value,
                            Name = wall.Name,
                            IsGrid = false,
                            IsWall = true,
                            ViewLine = line,
                            Reference = new Reference(wall)
                        };

                        // Calculate position
                        var dir = line.Direction.Normalize();
                        if (Math.Abs(dir.Y) > Math.Abs(dir.X))
                        {
                            dimRef.Position = line.GetEndPoint(0).X;
                        }
                        else
                        {
                            dimRef.Position = line.GetEndPoint(0).Y;
                        }

                        result.Add(dimRef);
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return result;
        }

        /// <summary>
        /// Groups openings into rows (horizontal) and columns (vertical) for chain dimensioning.
        /// </summary>
        public List<OpeningGroup> GroupOpenings(
            List<DimensionableOpening> openings,
            List<DimensionReference> references,
            DimensionSettings settings)
        {
            var result = new List<OpeningGroup>();

            // Sort openings by X and Y
            var sortedByY = openings.OrderBy(o => o.ViewCenter.Y).ToList();
            var sortedByX = openings.OrderBy(o => o.ViewCenter.X).ToList();

            // Group into horizontal rows (same Y within tolerance)
            if (settings.Direction == DimensionDirection.Horizontal || settings.Direction == DimensionDirection.Both)
            {
                var horizontalGroups = GroupByCoordinate(sortedByX, o => o.ViewCenter.Y, settings.GroupingTolerance);
                foreach (var group in horizontalGroups)
                {
                    if (group.Count < settings.MinOpeningsPerGroup) continue;

                    var openingGroup = new OpeningGroup
                    {
                        Openings = group.OrderBy(o => o.ViewCenter.X).ToList(),
                        Direction = DimensionDirection.Horizontal,
                        CommonCoordinate = group.Average(o => o.ViewCenter.Y)
                    };

                    // Find nearest vertical references (grids/walls that run N-S)
                    var verticalRefs = references
                        .Where(r => IsVerticalLine(r.ViewLine))
                        .OrderBy(r => r.Position)
                        .ToList();

                    if (verticalRefs.Count > 0)
                    {
                        var firstOpening = openingGroup.Openings.First();
                        var lastOpening = openingGroup.Openings.Last();

                        // Find nearest ref before first opening
                        openingGroup.StartReference = verticalRefs
                            .Where(r => r.Position < firstOpening.ViewCenter.X)
                            .OrderByDescending(r => r.Position)
                            .FirstOrDefault();

                        // Find nearest ref after last opening
                        openingGroup.EndReference = verticalRefs
                            .Where(r => r.Position > lastOpening.ViewCenter.X)
                            .OrderBy(r => r.Position)
                            .FirstOrDefault();
                    }

                    result.Add(openingGroup);
                }
            }

            // Group into vertical columns (same X within tolerance)
            if (settings.Direction == DimensionDirection.Vertical || settings.Direction == DimensionDirection.Both)
            {
                var verticalGroups = GroupByCoordinate(sortedByY, o => o.ViewCenter.X, settings.GroupingTolerance);
                foreach (var group in verticalGroups)
                {
                    if (group.Count < settings.MinOpeningsPerGroup) continue;

                    var openingGroup = new OpeningGroup
                    {
                        Openings = group.OrderBy(o => o.ViewCenter.Y).ToList(),
                        Direction = DimensionDirection.Vertical,
                        CommonCoordinate = group.Average(o => o.ViewCenter.X)
                    };

                    // Find nearest horizontal references (grids/walls that run E-W)
                    var horizontalRefs = references
                        .Where(r => IsHorizontalLine(r.ViewLine))
                        .OrderBy(r => r.Position)
                        .ToList();

                    if (horizontalRefs.Count > 0)
                    {
                        var firstOpening = openingGroup.Openings.First();
                        var lastOpening = openingGroup.Openings.Last();

                        openingGroup.StartReference = horizontalRefs
                            .Where(r => r.Position < firstOpening.ViewCenter.Y)
                            .OrderByDescending(r => r.Position)
                            .FirstOrDefault();

                        openingGroup.EndReference = horizontalRefs
                            .Where(r => r.Position > lastOpening.ViewCenter.Y)
                            .OrderBy(r => r.Position)
                            .FirstOrDefault();
                    }

                    result.Add(openingGroup);
                }
            }

            return result;
        }

        /// <summary>
        /// Groups items by a coordinate within tolerance.
        /// </summary>
        private List<List<DimensionableOpening>> GroupByCoordinate(
            List<DimensionableOpening> openings,
            Func<DimensionableOpening, double> coordinateSelector,
            double tolerance)
        {
            var result = new List<List<DimensionableOpening>>();
            var remaining = new List<DimensionableOpening>(openings);

            while (remaining.Count > 0)
            {
                var first = remaining[0];
                var coord = coordinateSelector(first);

                var group = remaining
                    .Where(o => Math.Abs(coordinateSelector(o) - coord) <= tolerance)
                    .ToList();

                result.Add(group);

                foreach (var o in group)
                {
                    remaining.Remove(o);
                }
            }

            return result;
        }

        private bool IsVerticalLine(Line line)
        {
            if (line == null) return false;
            var dir = line.Direction.Normalize();
            return Math.Abs(dir.Y) > Math.Abs(dir.X);
        }

        private bool IsHorizontalLine(Line line)
        {
            if (line == null) return false;
            var dir = line.Direction.Normalize();
            return Math.Abs(dir.X) > Math.Abs(dir.Y);
        }
    }
}
