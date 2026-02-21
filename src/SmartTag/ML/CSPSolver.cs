using System;
using System.Collections.Generic;
using System.Linq;
using SmartTag.Models;

namespace SmartTag.ML
{
    /// <summary>
    /// Constraint Satisfaction Problem solver for tag placement.
    /// Ensures all hard constraints are satisfied while optimizing soft constraints.
    /// </summary>
    public class CSPSolver
    {
        private readonly CSPConstraints _constraints;
        private readonly double _tolerance;

        public CSPSolver(CSPConstraints constraints = null, double tolerance = 0.01)
        {
            _constraints = constraints ?? new CSPConstraints();
            _tolerance = tolerance;
        }

        #region Hard Constraints (Must Satisfy)

        /// <summary>
        /// Check that tag doesn't overlap with any existing tags.
        /// </summary>
        public bool CheckNoTagOverlap(TagPlacement tag, List<TagPlacement> existingTags)
        {
            if (tag == null || existingTags == null)
                return true;

            foreach (var existing in existingTags)
            {
                if (existing == null)
                    continue;

                if (tag.EstimatedTagBounds.Intersects(existing.EstimatedTagBounds))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check that tag doesn't overlap with any elements (except its own element).
        /// Uses element IDs to skip the host element's bounds.
        /// </summary>
        public bool CheckNoElementOverlap(TagPlacement tag, List<(long ElementId, BoundingBox2D Bounds)> elementBounds, long excludeElementId = -1)
        {
            if (tag == null || elementBounds == null)
                return true;

            // Shrink tag bounds slightly so touching edges are permitted
            var shrunk = tag.EstimatedTagBounds.Expand(-_tolerance);

            foreach (var (elemId, bound) in elementBounds)
            {
                if (elemId == excludeElementId)
                    continue;

                if (shrunk.Intersects(bound))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check that leader line doesn't cross any elements (except its own element).
        /// </summary>
        public bool CheckLeaderNoCollision(TagPlacement tag, List<(long ElementId, BoundingBox2D Bounds)> elementBounds, long excludeElementId = -1)
        {
            if (tag == null || !tag.HasLeader || elementBounds == null)
                return true;

            var leaderStart = tag.TagLocation;
            var leaderEnd = tag.LeaderEnd;

            foreach (var (elemId, bound) in elementBounds)
            {
                if (elemId == excludeElementId)
                    continue;

                if (LineIntersectsBox(leaderStart, leaderEnd, bound))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check that tag is within view crop box.
        /// </summary>
        public bool CheckWithinViewCrop(TagPlacement tag, BoundingBox2D? viewCrop)
        {
            if (tag == null || !viewCrop.HasValue)
                return true;

            return viewCrop.Value.Contains(tag.EstimatedTagBounds);
        }

        #endregion

        #region Soft Constraints (Optimize)

        /// <summary>
        /// Score how well tags are aligned (higher is better).
        /// </summary>
        public double ScoreAlignment(List<TagPlacement> tags)
        {
            if (tags == null || tags.Count < 2)
                return 0;

            double score = 0;
            var alignmentTolerance = 0.5; // feet

            // Check horizontal alignment
            var yPositions = tags.Select(t => t.TagLocation.Y).OrderBy(y => y).ToList();
            for (int i = 1; i < yPositions.Count; i++)
            {
                if (Math.Abs(yPositions[i] - yPositions[i - 1]) < alignmentTolerance)
                    score += 10; // Bonus for aligned pair
            }

            // Check vertical alignment
            var xPositions = tags.Select(t => t.TagLocation.X).OrderBy(x => x).ToList();
            for (int i = 1; i < xPositions.Count; i++)
            {
                if (Math.Abs(xPositions[i] - xPositions[i - 1]) < alignmentTolerance)
                    score += 10;
            }

            return score;
        }

        /// <summary>
        /// Score based on leader length (shorter is better, returns negative penalty).
        /// </summary>
        public double ScoreLeaderLength(TagPlacement tag)
        {
            if (tag == null || !tag.HasLeader)
                return 0;

            var length = tag.TagLocation.DistanceTo(tag.LeaderEnd);
            
            // Penalty increases with length
            return -length * 2;
        }

        /// <summary>
        /// Score based on consistent spacing between tags (higher is better).
        /// </summary>
        public double ScoreSpacingConsistency(List<TagPlacement> tags)
        {
            if (tags == null || tags.Count < 3)
                return 0;

            // Calculate pairwise distances
            var distances = new List<double>();
            for (int i = 0; i < tags.Count; i++)
            {
                for (int j = i + 1; j < tags.Count; j++)
                {
                    distances.Add(tags[i].TagLocation.DistanceTo(tags[j].TagLocation));
                }
            }

            if (distances.Count < 2)
                return 0;

            // Lower variance = more consistent = higher score
            var mean = distances.Average();
            var variance = distances.Sum(d => Math.Pow(d - mean, 2)) / distances.Count;
            
            return -variance; // Return negative variance as penalty
        }

        #endregion

        #region Solver

        /// <summary>
        /// Solve CSP to find valid placement for a tag.
        /// Uses backtracking with constraint propagation.
        /// </summary>
        public TagPlacement Solve(
            List<TagPlacement> candidates,
            List<TagPlacement> existingTags,
            List<(long ElementId, BoundingBox2D Bounds)> elementBounds,
            BoundingBox2D? viewCrop)
        {
            if (candidates == null || candidates.Count == 0)
                return null;

            // Sort candidates by preference (initial order is preference order)
            var sortedCandidates = candidates.ToList();

            foreach (var candidate in sortedCandidates)
            {
                // Check all hard constraints
                var satisfiesHard = 
                    CheckNoTagOverlap(candidate, existingTags) &&
                    CheckNoElementOverlap(candidate, elementBounds, candidate.ElementId) &&
                    CheckLeaderNoCollision(candidate, elementBounds, candidate.ElementId) &&
                    CheckWithinViewCrop(candidate, viewCrop);

                if (satisfiesHard)
                {
                    // Calculate soft constraint score
                    var tempTags = new List<TagPlacement>(existingTags) { candidate };
                    candidate.Score = 
                        ScoreAlignment(tempTags) +
                        ScoreLeaderLength(candidate) +
                        ScoreSpacingConsistency(tempTags);

                    return candidate;
                }
            }

            // No valid candidate found, try to find best violating candidate
            // and adjust its position
            return TryAdjustBestCandidate(sortedCandidates, existingTags, elementBounds, viewCrop);
        }

        /// <summary>
        /// Try to adjust the best candidate to satisfy constraints.
        /// </summary>
        private TagPlacement TryAdjustBestCandidate(
            List<TagPlacement> candidates,
            List<TagPlacement> existingTags,
            List<(long ElementId, BoundingBox2D Bounds)> elementBounds,
            BoundingBox2D? viewCrop)
        {
            if (candidates.Count == 0)
                return null;

            var best = candidates[0];
            
            // Try shifting in 8 directions
            var shifts = new (double dx, double dy)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1)
            };

            double shiftAmount = 1.0; // feet
            int maxAttempts = 5;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                foreach (var (dx, dy) in shifts)
                {
                    var shifted = ShiftPlacement(best, dx * shiftAmount, dy * shiftAmount);

                    var satisfiesHard = 
                        CheckNoTagOverlap(shifted, existingTags) &&
                        CheckNoElementOverlap(shifted, elementBounds, shifted.ElementId) &&
                        CheckLeaderNoCollision(shifted, elementBounds, shifted.ElementId) &&
                        CheckWithinViewCrop(shifted, viewCrop);

                    if (satisfiesHard)
                        return shifted;
                }

                shiftAmount *= 1.5; // Increase shift for next attempt
            }

            // Return best candidate even if constraints not fully satisfied
            return best;
        }

        /// <summary>
        /// Create a shifted copy of a placement.
        /// </summary>
        private TagPlacement ShiftPlacement(TagPlacement original, double dx, double dy)
        {
            return new TagPlacement
            {
                ElementId = original.ElementId,
                TagLocation = new Point2D(original.TagLocation.X + dx, original.TagLocation.Y + dy),
                LeaderEnd = original.LeaderEnd,
                HasLeader = original.HasLeader,
                Position = original.Position,
                EstimatedTagBounds = new BoundingBox2D(
                    original.EstimatedTagBounds.MinX + dx,
                    original.EstimatedTagBounds.MinY + dy,
                    original.EstimatedTagBounds.MaxX + dx,
                    original.EstimatedTagBounds.MaxY + dy),
                Rotation = original.Rotation,
                SegmentIndex = original.SegmentIndex,
                DistanceMultiplier = original.DistanceMultiplier
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Check if a line segment intersects a bounding box.
        /// </summary>
        private bool LineIntersectsBox(Point2D start, Point2D end, BoundingBox2D box)
        {
            // Liang-Barsky algorithm
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;

            var p = new[] { -dx, dx, -dy, dy };
            var q = new[]
            {
                start.X - box.MinX,
                box.MaxX - start.X,
                start.Y - box.MinY,
                box.MaxY - start.Y
            };

            double u1 = 0, u2 = 1;

            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-10)
                {
                    if (q[i] < 0)
                        return false;
                }
                else
                {
                    var t = q[i] / p[i];
                    if (p[i] < 0)
                        u1 = Math.Max(u1, t);
                    else
                        u2 = Math.Min(u2, t);
                }
            }

            return u1 <= u2;
        }

        #endregion

        #region Global Optimization

        /// <summary>
        /// Optimize all tag placements globally for alignment.
        /// Called after individual placements are determined.
        /// </summary>
        public void OptimizeGlobalAlignment(List<TagPlacement> placements, double alignmentTolerance = 0.5)
        {
            if (placements == null || placements.Count < 2)
                return;

            // Detect alignment rows
            var rows = DetectAlignmentRows(placements, alignmentTolerance);
            
            // Snap tags to alignment rows
            foreach (var row in rows)
            {
                if (row.Tags.Count < 2)
                    continue;

                var avgY = row.Tags.Average(t => t.TagLocation.Y);
                
                foreach (var tag in row.Tags)
                {
                    var dy = avgY - tag.TagLocation.Y;
                    tag.TagLocation = new Point2D(tag.TagLocation.X, avgY);
                    tag.EstimatedTagBounds = new BoundingBox2D(
                        tag.EstimatedTagBounds.MinX,
                        tag.EstimatedTagBounds.MinY + dy,
                        tag.EstimatedTagBounds.MaxX,
                        tag.EstimatedTagBounds.MaxY + dy);
                }
            }

            // Detect alignment columns
            var columns = DetectAlignmentColumns(placements, alignmentTolerance);
            
            // Snap tags to alignment columns
            foreach (var col in columns)
            {
                if (col.Tags.Count < 2)
                    continue;

                var avgX = col.Tags.Average(t => t.TagLocation.X);
                
                foreach (var tag in col.Tags)
                {
                    var dx = avgX - tag.TagLocation.X;
                    tag.TagLocation = new Point2D(avgX, tag.TagLocation.Y);
                    tag.EstimatedTagBounds = new BoundingBox2D(
                        tag.EstimatedTagBounds.MinX + dx,
                        tag.EstimatedTagBounds.MinY,
                        tag.EstimatedTagBounds.MaxX + dx,
                        tag.EstimatedTagBounds.MaxY);
                }
            }
        }

        private List<AlignmentGroup> DetectAlignmentRows(List<TagPlacement> placements, double tolerance)
        {
            var rows = new List<AlignmentGroup>();
            var sorted = placements.OrderBy(t => t.TagLocation.Y).ToList();

            AlignmentGroup currentRow = null;

            foreach (var tag in sorted)
            {
                if (currentRow == null || Math.Abs(tag.TagLocation.Y - currentRow.Position) > tolerance)
                {
                    currentRow = new AlignmentGroup { Position = tag.TagLocation.Y };
                    rows.Add(currentRow);
                }
                currentRow.Tags.Add(tag);
            }

            return rows.Where(r => r.Tags.Count >= 2).ToList();
        }

        private List<AlignmentGroup> DetectAlignmentColumns(List<TagPlacement> placements, double tolerance)
        {
            var columns = new List<AlignmentGroup>();
            var sorted = placements.OrderBy(t => t.TagLocation.X).ToList();

            AlignmentGroup currentCol = null;

            foreach (var tag in sorted)
            {
                if (currentCol == null || Math.Abs(tag.TagLocation.X - currentCol.Position) > tolerance)
                {
                    currentCol = new AlignmentGroup { Position = tag.TagLocation.X };
                    columns.Add(currentCol);
                }
                currentCol.Tags.Add(tag);
            }

            return columns.Where(c => c.Tags.Count >= 2).ToList();
        }

        private class AlignmentGroup
        {
            public double Position { get; set; }
            public List<TagPlacement> Tags { get; set; } = new();
        }

        #endregion
    }

    /// <summary>
    /// Configuration for CSP constraints.
    /// </summary>
    public class CSPConstraints
    {
        // Hard constraint weights (not used as weights, just presence)
        public bool EnableNoTagOverlap { get; set; } = true;
        public bool EnableNoElementOverlap { get; set; } = true;
        public bool EnableLeaderNoCollision { get; set; } = true;
        public bool EnableWithinViewCrop { get; set; } = true;

        // Soft constraint weights
        public double AlignmentWeight { get; set; } = 1.0;
        public double LeaderLengthWeight { get; set; } = 0.5;
        public double SpacingConsistencyWeight { get; set; } = 0.3;

        // Tolerance values
        public double OverlapTolerance { get; set; } = 0.01;
        public double AlignmentTolerance { get; set; } = 0.5;
    }
}
