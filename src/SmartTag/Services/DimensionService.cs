using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Creates automatic dimensions for openings in Revit views.
    /// Pattern: Chain dimensions from Grid → Opening centers.
    /// </summary>
    public class DimensionService
    {
        private readonly Document _doc;
        private readonly View _view;

        public DimensionService(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <summary>
        /// Creates automatic dimensions for all openings in the view.
        /// </summary>
        public DimensionResult CreateAutoDimensions(DimensionSettings settings)
        {
            var sw = Stopwatch.StartNew();
            var result = new DimensionResult();

            try
            {
                // Step 1: Collect openings
                var collector = new OpeningCollector(_doc, _view);
                var openings = collector.CollectOpenings(settings);
                result.OpeningsProcessed = openings.Count;

                if (openings.Count == 0)
                {
                    result.Warnings.Add("No openings found in the current view.");
                    return result;
                }

                // Step 2: Collect reference elements (grids, walls)
                var references = new List<DimensionReference>();
                if (settings.UseGrids)
                {
                    references.AddRange(collector.CollectGrids());
                }
                if (settings.UseWalls)
                {
                    references.AddRange(collector.CollectWalls());
                }

                // Step 3: Group openings into rows/columns
                var groups = collector.GroupOpenings(openings, references, settings);
                result.GroupsProcessed = groups.Count;

                if (groups.Count == 0)
                {
                    result.Warnings.Add("Could not group openings for dimensioning.");
                    return result;
                }

                // Step 4: Create dimensions for each group
                using (var trans = new Transaction(_doc, "SmartTag: Auto Dimension Openings"))
                {
                    trans.Start();

                    foreach (var group in groups)
                    {
                        try
                        {
                            var dimIds = CreateGroupDimensions(group, settings);
                            result.CreatedDimensionIds.AddRange(dimIds);
                            result.DimensionsCreated += dimIds.Count;
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"Failed to dimension group: {ex.Message}");
                            result.Skipped++;
                        }
                    }

                    trans.Commit();
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Auto dimension failed: {ex.Message}");
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>
        /// Creates chain dimension for an opening group.
        /// </summary>
        private List<long> CreateGroupDimensions(OpeningGroup group, DimensionSettings settings)
        {
            var dimIds = new List<long>();

            // Build reference array for chain dimension
            var refArray = new ReferenceArray();

            // Add start reference (grid/wall)
            if (group.StartReference?.Reference != null && settings.Mode != DimensionMode.BetweenOpenings)
            {
                refArray.Append(group.StartReference.Reference);
            }

            // Add opening center references
            foreach (var opening in group.Openings)
            {
                if (opening.CenterReference != null)
                {
                    refArray.Append(opening.CenterReference);
                }
            }

            // Add end reference (grid/wall)
            if (group.EndReference?.Reference != null && settings.Mode != DimensionMode.BetweenOpenings)
            {
                refArray.Append(group.EndReference.Reference);
            }

            // Need at least 2 references for a dimension
            if (refArray.Size < 2)
            {
                return dimIds;
            }

            // Calculate dimension line position
            var dimLine = CalculateDimensionLine(group, settings);
            if (dimLine == null)
            {
                return dimIds;
            }

            // Create the dimension
            try
            {
                var dim = _doc.Create.NewDimension(_view, dimLine, refArray);
                if (dim != null)
                {
                    dimIds.Add(dim.Id.Value);

                    // Apply dimension type if specified
                    if (settings.DimensionTypeId.HasValue)
                    {
                        var typeId = new ElementId(settings.DimensionTypeId.Value);
                        var dimType = _doc.GetElement(typeId) as DimensionType;
                        if (dimType != null)
                        {
                            dim.DimensionType = dimType;
                        }
                    }
                }
            }
            catch
            {
                // Try alternative approach with individual dimensions between pairs
                dimIds.AddRange(CreatePairDimensions(group, settings));
            }

            return dimIds;
        }

        /// <summary>
        /// Creates individual dimensions between consecutive elements when chain dimension fails.
        /// </summary>
        private List<long> CreatePairDimensions(OpeningGroup group, DimensionSettings settings)
        {
            var dimIds = new List<long>();
            var allPoints = new List<(XYZ Point, Reference Ref)>();

            // Collect all points and references
            if (group.StartReference?.Reference != null)
            {
                var startPoint = GetReferencePoint(group.StartReference, group.Openings.First().CenterPoint);
                if (startPoint != null)
                {
                    allPoints.Add((startPoint, group.StartReference.Reference));
                }
            }

            foreach (var opening in group.Openings)
            {
                if (opening.CenterReference != null)
                {
                    allPoints.Add((opening.CenterPoint, opening.CenterReference));
                }
            }

            if (group.EndReference?.Reference != null)
            {
                var endPoint = GetReferencePoint(group.EndReference, group.Openings.Last().CenterPoint);
                if (endPoint != null)
                {
                    allPoints.Add((endPoint, group.EndReference.Reference));
                }
            }

            // Create dimension between each consecutive pair
            for (int i = 0; i < allPoints.Count - 1; i++)
            {
                try
                {
                    var refArray = new ReferenceArray();
                    refArray.Append(allPoints[i].Ref);
                    refArray.Append(allPoints[i + 1].Ref);

                    var midPoint = (allPoints[i].Point + allPoints[i + 1].Point) / 2.0;
                    var dimLine = CalculatePairDimensionLine(allPoints[i].Point, allPoints[i + 1].Point, group.Direction, settings);

                    if (dimLine != null)
                    {
                        var dim = _doc.Create.NewDimension(_view, dimLine, refArray);
                        if (dim != null)
                        {
                            dimIds.Add(dim.Id.Value);
                        }
                    }
                }
                catch
                {
                    // Skip failed pair
                }
            }

            return dimIds;
        }

        /// <summary>
        /// Calculates the dimension line for a group.
        /// </summary>
        private Line CalculateDimensionLine(OpeningGroup group, DimensionSettings settings)
        {
            if (group.Openings.Count == 0) return null;

            var offset = settings.DimensionOffset;

            if (group.Direction == DimensionDirection.Horizontal)
            {
                // Horizontal chain dimension
                // Line runs parallel to X axis, offset above or below the openings
                var minX = group.Openings.Min(o => o.CenterPoint.X);
                var maxX = group.Openings.Max(o => o.CenterPoint.X);

                // Extend to include grid references
                if (group.StartReference != null)
                {
                    minX = Math.Min(minX, group.StartReference.Position);
                }
                if (group.EndReference != null)
                {
                    maxX = Math.Max(maxX, group.EndReference.Position);
                }

                var y = group.CommonCoordinate + offset;
                var z = group.Openings.First().CenterPoint.Z;

                var start = new XYZ(minX - 1, y, z);
                var end = new XYZ(maxX + 1, y, z);

                return Line.CreateBound(start, end);
            }
            else
            {
                // Vertical chain dimension
                // Line runs parallel to Y axis, offset left or right
                var minY = group.Openings.Min(o => o.CenterPoint.Y);
                var maxY = group.Openings.Max(o => o.CenterPoint.Y);

                // Extend to include grid references
                if (group.StartReference != null)
                {
                    minY = Math.Min(minY, group.StartReference.Position);
                }
                if (group.EndReference != null)
                {
                    maxY = Math.Max(maxY, group.EndReference.Position);
                }

                var x = group.CommonCoordinate + offset;
                var z = group.Openings.First().CenterPoint.Z;

                var start = new XYZ(x, minY - 1, z);
                var end = new XYZ(x, maxY + 1, z);

                return Line.CreateBound(start, end);
            }
        }

        /// <summary>
        /// Calculates dimension line for a pair of points.
        /// </summary>
        private Line CalculatePairDimensionLine(XYZ p1, XYZ p2, DimensionDirection direction, DimensionSettings settings)
        {
            var offset = settings.DimensionOffset;

            if (direction == DimensionDirection.Horizontal)
            {
                var y = (p1.Y + p2.Y) / 2.0 + offset;
                var z = (p1.Z + p2.Z) / 2.0;
                return Line.CreateBound(
                    new XYZ(p1.X, y, z),
                    new XYZ(p2.X, y, z));
            }
            else
            {
                var x = (p1.X + p2.X) / 2.0 + offset;
                var z = (p1.Z + p2.Z) / 2.0;
                return Line.CreateBound(
                    new XYZ(x, p1.Y, z),
                    new XYZ(x, p2.Y, z));
            }
        }

        /// <summary>
        /// Gets a point on a reference element (grid/wall) at the level of the opening.
        /// </summary>
        private XYZ GetReferencePoint(DimensionReference reference, XYZ openingPoint)
        {
            if (reference?.ViewLine == null) return null;

            var line = reference.ViewLine;
            var dir = line.Direction.Normalize();

            // Project opening point onto the reference line
            var lineStart = line.GetEndPoint(0);
            var toOpening = openingPoint - lineStart;

            if (Math.Abs(dir.Y) > Math.Abs(dir.X))
            {
                // Vertical line - use its X and opening's Y
                return new XYZ(lineStart.X, openingPoint.Y, openingPoint.Z);
            }
            else
            {
                // Horizontal line - use its Y and opening's X
                return new XYZ(openingPoint.X, lineStart.Y, openingPoint.Z);
            }
        }

        /// <summary>
        /// Gets available dimension types.
        /// </summary>
        public List<(long Id, string Name)> GetDimensionTypes()
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(DimensionType))
                .WhereElementIsElementType()
                .Cast<DimensionType>()
                .Where(dt => dt.StyleType == DimensionStyleType.Linear)
                .Select(dt => (dt.Id.Value, dt.Name))
                .OrderBy(t => t.Name)
                .ToList();
        }

        /// <summary>
        /// Creates dimensions for selected elements only.
        /// </summary>
        public DimensionResult CreateDimensionsForSelection(ICollection<ElementId> selectedIds, DimensionSettings settings)
        {
            var sw = Stopwatch.StartNew();
            var result = new DimensionResult();

            if (selectedIds == null || selectedIds.Count == 0)
            {
                result.Warnings.Add("No elements selected.");
                return result;
            }

            try
            {
                // Collect only selected openings
                var collector = new OpeningCollector(_doc, _view);
                var allOpenings = collector.CollectOpenings(settings);
                var selectedOpenings = allOpenings
                    .Where(o => selectedIds.Any(id => id.Value == o.ElementId))
                    .ToList();

                result.OpeningsProcessed = selectedOpenings.Count;

                if (selectedOpenings.Count == 0)
                {
                    result.Warnings.Add("No openings found in selection.");
                    return result;
                }

                // Collect references and group
                var references = new List<DimensionReference>();
                if (settings.UseGrids) references.AddRange(collector.CollectGrids());
                if (settings.UseWalls) references.AddRange(collector.CollectWalls());

                var groups = collector.GroupOpenings(selectedOpenings, references, settings);
                result.GroupsProcessed = groups.Count;

                // Create dimensions
                using (var trans = new Transaction(_doc, "SmartTag: Dimension Selection"))
                {
                    trans.Start();

                    foreach (var group in groups)
                    {
                        try
                        {
                            var dimIds = CreateGroupDimensions(group, settings);
                            result.CreatedDimensionIds.AddRange(dimIds);
                            result.DimensionsCreated += dimIds.Count;
                        }
                        catch (Exception ex)
                        {
                            result.Warnings.Add($"Failed: {ex.Message}");
                            result.Skipped++;
                        }
                    }

                    trans.Commit();
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed: {ex.Message}");
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }
    }
}
