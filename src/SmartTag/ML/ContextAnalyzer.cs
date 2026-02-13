using System;
using System.Collections.Generic;
using System.Linq;
using SmartTag.Models;

namespace SmartTag.ML
{
    /// <summary>
    /// Analyzes the context around an element for intelligent tag placement.
    /// </summary>
    public class ContextAnalyzer
    {
        private readonly double _neighborRadius;
        private readonly double _wallDetectionRadius;

        public ContextAnalyzer(double neighborRadius = 10.0, double wallDetectionRadius = 5.0)
        {
            _neighborRadius = neighborRadius;
            _wallDetectionRadius = wallDetectionRadius;
        }

        /// <summary>
        /// Analyze the context around an element.
        /// </summary>
        public ElementContext Analyze(TaggableElement element, List<TaggableElement> allElements)
        {
            if (element == null)
                return new ElementContext();

            var context = new ElementContext();
            var center = element.Center;
            var bounds = element.ViewBounds;

            // Find neighbors
            var neighbors = allElements
                .Where(e => e.ElementId != element.ElementId)
                .Where(e => e.Center.DistanceTo(center) < _neighborRadius)
                .ToList();

            context.NeighborCount = neighbors.Count;

            // Determine density
            context.Density = neighbors.Count switch
            {
                <= 2 => DensityLevel.Low,
                <= 6 => DensityLevel.Medium,
                _ => DensityLevel.High
            };

            // Check directional neighbors
            AnalyzeDirectionalNeighbors(element, neighbors, context);

            // Count parallel elements (same orientation for linear elements)
            if (element.IsLinearElement)
            {
                context.ParallelElementsCount = CountParallelElements(element, neighbors);
            }

            // Check if in group
            context.IsInGroup = element.IsInGroup;

            // Distance to wall (placeholder - would need wall elements)
            context.DistanceToWall = EstimateDistanceToWall(element, allElements);

            return context;
        }

        /// <summary>
        /// Analyze neighbors in each direction.
        /// </summary>
        private void AnalyzeDirectionalNeighbors(
            TaggableElement element,
            List<TaggableElement> neighbors,
            ElementContext context)
        {
            var center = element.Center;
            var bounds = element.ViewBounds;
            
            // Define direction zones
            var halfWidth = bounds.Width / 2 + 1.0; // Tolerance
            var halfHeight = bounds.Height / 2 + 1.0;

            context.DistanceToNearestAbove = double.MaxValue;
            context.DistanceToNearestBelow = double.MaxValue;
            context.DistanceToNearestLeft = double.MaxValue;
            context.DistanceToNearestRight = double.MaxValue;

            foreach (var neighbor in neighbors)
            {
                var nc = neighbor.Center;
                var dx = nc.X - center.X;
                var dy = nc.Y - center.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);

                // Above: dy > halfHeight and |dx| < halfWidth * 2
                if (dy > halfHeight && Math.Abs(dx) < halfWidth * 2)
                {
                    context.HasNeighborAbove = true;
                    if (dy < context.DistanceToNearestAbove)
                        context.DistanceToNearestAbove = dy;
                }

                // Below: dy < -halfHeight and |dx| < halfWidth * 2
                if (dy < -halfHeight && Math.Abs(dx) < halfWidth * 2)
                {
                    context.HasNeighborBelow = true;
                    if (-dy < context.DistanceToNearestBelow)
                        context.DistanceToNearestBelow = -dy;
                }

                // Right: dx > halfWidth and |dy| < halfHeight * 2
                if (dx > halfWidth && Math.Abs(dy) < halfHeight * 2)
                {
                    context.HasNeighborRight = true;
                    if (dx < context.DistanceToNearestRight)
                        context.DistanceToNearestRight = dx;
                }

                // Left: dx < -halfWidth and |dy| < halfHeight * 2
                if (dx < -halfWidth && Math.Abs(dy) < halfHeight * 2)
                {
                    context.HasNeighborLeft = true;
                    if (-dx < context.DistanceToNearestLeft)
                        context.DistanceToNearestLeft = -dx;
                }
            }

            // Reset MaxValue to reasonable defaults
            if (context.DistanceToNearestAbove == double.MaxValue)
                context.DistanceToNearestAbove = _neighborRadius;
            if (context.DistanceToNearestBelow == double.MaxValue)
                context.DistanceToNearestBelow = _neighborRadius;
            if (context.DistanceToNearestLeft == double.MaxValue)
                context.DistanceToNearestLeft = _neighborRadius;
            if (context.DistanceToNearestRight == double.MaxValue)
                context.DistanceToNearestRight = _neighborRadius;
        }

        /// <summary>
        /// Count elements that are parallel to the given element.
        /// </summary>
        private int CountParallelElements(TaggableElement element, List<TaggableElement> neighbors)
        {
            if (!element.IsLinearElement)
                return 0;

            var angleTolerance = Math.PI / 18; // 10 degrees
            var elementAngle = element.AngleRadians;

            return neighbors.Count(n => 
                n.IsLinearElement && 
                Math.Abs(NormalizeAngle(n.AngleRadians - elementAngle)) < angleTolerance);
        }

        /// <summary>
        /// Estimate distance to nearest wall.
        /// </summary>
        private double EstimateDistanceToWall(TaggableElement element, List<TaggableElement> allElements)
        {
            // For now, estimate based on boundary of all elements
            // In production, would query actual walls from Revit

            var minX = allElements.Min(e => e.ViewBounds.MinX);
            var maxX = allElements.Max(e => e.ViewBounds.MaxX);
            var minY = allElements.Min(e => e.ViewBounds.MinY);
            var maxY = allElements.Max(e => e.ViewBounds.MaxY);

            var distToLeft = element.Center.X - minX;
            var distToRight = maxX - element.Center.X;
            var distToBottom = element.Center.Y - minY;
            var distToTop = maxY - element.Center.Y;

            return Math.Min(Math.Min(distToLeft, distToRight), Math.Min(distToBottom, distToTop));
        }

        /// <summary>
        /// Normalize angle to [-π, π].
        /// </summary>
        private double NormalizeAngle(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }

        /// <summary>
        /// Suggest best tag positions based on context.
        /// </summary>
        public List<TagPosition> SuggestPositions(TaggableElement element, ElementContext context)
        {
            var suggestions = new List<TagPosition>();

            // For horizontal linear elements (pipes/ducts)
            if (element.IsLinearElement)
            {
                var isHorizontal = Math.Abs(Math.Sin(element.AngleRadians)) < 0.5;

                if (isHorizontal)
                {
                    // Prefer top/bottom for horizontal elements
                    if (!context.HasNeighborAbove)
                        suggestions.Add(TagPosition.TopCenter);
                    if (!context.HasNeighborBelow)
                        suggestions.Add(TagPosition.BottomCenter);
                }
                else
                {
                    // Prefer left/right for vertical elements
                    if (!context.HasNeighborRight)
                        suggestions.Add(TagPosition.Right);
                    if (!context.HasNeighborLeft)
                        suggestions.Add(TagPosition.Left);
                }
            }

            // For equipment/fixtures
            else
            {
                // Prefer corners for equipment
                if (!context.HasNeighborAbove && !context.HasNeighborRight)
                    suggestions.Add(TagPosition.TopRight);
                if (!context.HasNeighborAbove && !context.HasNeighborLeft)
                    suggestions.Add(TagPosition.TopLeft);
                if (!context.HasNeighborBelow && !context.HasNeighborRight)
                    suggestions.Add(TagPosition.BottomRight);
                if (!context.HasNeighborBelow && !context.HasNeighborLeft)
                    suggestions.Add(TagPosition.BottomLeft);
            }

            // Fallback to all positions if nothing suggested
            if (suggestions.Count == 0)
            {
                suggestions.AddRange(new[]
                {
                    TagPosition.TopRight,
                    TagPosition.TopCenter,
                    TagPosition.TopLeft,
                    TagPosition.Right,
                    TagPosition.Left,
                    TagPosition.BottomRight,
                    TagPosition.BottomCenter,
                    TagPosition.BottomLeft
                });
            }

            return suggestions;
        }

        /// <summary>
        /// Calculate optimal offset based on context.
        /// </summary>
        public (double offsetX, double offsetY) CalculateOptimalOffset(
            TaggableElement element,
            ElementContext context,
            TagPosition position,
            double tagWidth,
            double tagHeight)
        {
            var baseOffsetX = element.ViewBounds.Width / 2 + tagWidth / 2 + 0.5;
            var baseOffsetY = element.ViewBounds.Height / 2 + tagHeight / 2 + 0.5;

            // Adjust based on density
            var densityMultiplier = context.Density switch
            {
                DensityLevel.Low => 0.8,
                DensityLevel.Medium => 1.0,
                DensityLevel.High => 1.3,
                _ => 1.0
            };

            baseOffsetX *= densityMultiplier;
            baseOffsetY *= densityMultiplier;

            return position switch
            {
                TagPosition.TopLeft => (-baseOffsetX, baseOffsetY),
                TagPosition.TopCenter => (0, baseOffsetY),
                TagPosition.TopRight => (baseOffsetX, baseOffsetY),
                TagPosition.Left => (-baseOffsetX, 0),
                TagPosition.Center => (0, 0),
                TagPosition.Right => (baseOffsetX, 0),
                TagPosition.BottomLeft => (-baseOffsetX, -baseOffsetY),
                TagPosition.BottomCenter => (0, -baseOffsetY),
                TagPosition.BottomRight => (baseOffsetX, -baseOffsetY),
                _ => (baseOffsetX, baseOffsetY)
            };
        }
    }
}
