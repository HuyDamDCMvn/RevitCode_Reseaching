using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Core service for calculating optimal tag placements.
    /// Implements Quick Mode (greedy heuristics) algorithm.
    /// </summary>
    public class TagPlacementService
    {
        // Base tag size (in feet at 1:100 scale)
        private const double BASE_TAG_WIDTH = 1.5;
        private const double BASE_TAG_HEIGHT = 0.4;
        private const double BASE_LEADER_LENGTH = 0.5;
        private const double MIN_SPACING = 0.2;
        
        // Score thresholds
        private const double MAX_ACCEPTABLE_SCORE = 300; // Skip tag if all positions worse than this
        private const double COLLISION_PENALTY = 100;
        private const double ELEMENT_COLLISION_PENALTY = 50;
        private const double LEADER_COLLISION_PENALTY = 30;
        
        // Estimated characters per foot width (for dynamic sizing)
        private const double CHARS_PER_FOOT = 8.0;
        
        // Linear element thresholds
        private const double LINEAR_SEGMENT_LENGTH = 20.0; // feet - split if longer
        private const double MIN_LINEAR_LENGTH_FOR_SPLIT = 30.0; // feet - only split if element is this long
        
        // Angle thresholds for tag rotation
        private const double VERTICAL_ANGLE_THRESHOLD = Math.PI / 4; // 45 degrees
        
        // Rule engine for preferred positions
        private RuleEngine _ruleEngine;

        private readonly Document _doc;
        private readonly View _view;
        private readonly SpatialIndex _elementIndex;
        private readonly SpatialIndex _tagIndex;
        private readonly SpatialIndex _annotationIndex; // For dimensions, text notes
        private readonly double _viewScale;
        private readonly BoundingBox2D? _viewCropBox;

        // Dynamic sizing
        private double _tagWidth;
        private double _tagHeight;
        private double _leaderLength;

        public TagPlacementService(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            
            // Get view scale factor (1:100 = 100, 1:50 = 50)
            _viewScale = view.Scale > 0 ? view.Scale : 100;
            var scaleFactor = _viewScale / 100.0;
            
            // Scale base sizes
            _tagWidth = BASE_TAG_WIDTH * scaleFactor;
            _tagHeight = BASE_TAG_HEIGHT * scaleFactor;
            _leaderLength = BASE_LEADER_LENGTH * scaleFactor;
            
            // Adjust cell size based on scale
            var cellSize = Math.Max(2.0 * scaleFactor, 1.0);
            _elementIndex = new SpatialIndex(cellSize);
            _tagIndex = new SpatialIndex(cellSize);
            _annotationIndex = new SpatialIndex(cellSize);
            
            // Get view crop box
            _viewCropBox = GetViewCropBox();
            
            // Initialize rule engine
            _ruleEngine = RuleEngine.Instance;
            _ruleEngine.Initialize();
        }
        
        private BoundingBox2D? GetViewCropBox()
        {
            try
            {
                if (!_view.CropBoxActive) return null;
                
                var cropBox = _view.CropBox;
                if (cropBox == null) return null;
                
                // Transform to view coordinates
                var origin = _view.Origin;
                var rightDir = _view.RightDirection;
                var upDir = _view.UpDirection;
                
                var minPt = cropBox.Min - origin;
                var maxPt = cropBox.Max - origin;
                
                return new BoundingBox2D(
                    minPt.DotProduct(rightDir),
                    minPt.DotProduct(upDir),
                    maxPt.DotProduct(rightDir),
                    maxPt.DotProduct(upDir));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Initialize the spatial indices with elements and existing tags.
        /// </summary>
        public void Initialize(List<TaggableElement> elements, 
            List<(long TagId, long HostId, BoundingBox2D Bounds)> existingTags,
            List<BoundingBox2D> annotations = null)
        {
            _elementIndex.Clear();
            _tagIndex.Clear();
            _annotationIndex.Clear();

            // Add elements to index
            foreach (var elem in elements)
            {
                _elementIndex.Add(elem.ElementId, elem.ViewBounds, elem);
            }

            // Add existing tags to index
            foreach (var (tagId, _, bounds) in existingTags)
            {
                _tagIndex.Add(tagId, bounds);
            }
            
            // Add annotations (dimensions, text notes) to separate index
            if (annotations != null)
            {
                for (int i = 0; i < annotations.Count; i++)
                {
                    _annotationIndex.Add(i + 2000000, annotations[i]);
                }
            }
        }
        
        /// <summary>
        /// Estimate tag size based on text content.
        /// </summary>
        private (double width, double height) EstimateTagSize(TaggableElement element)
        {
            try
            {
                // Get generated tag text if available
                var text = element?.GeneratedTagText ?? element?.SizeString ?? "DN100";
                if (string.IsNullOrEmpty(text)) text = "DN100";
                
                var lines = text.Split('\n');
                if (lines == null || lines.Length == 0) 
                    return (_tagWidth, _tagHeight);
                
                var maxLineLength = lines.Max(l => l?.Length ?? 0);
                if (maxLineLength == 0) maxLineLength = 5;
                
                var lineCount = lines.Length;
                
                // Scale-adjusted sizing
                var scaleFactor = _viewScale / 100.0;
                if (scaleFactor <= 0) scaleFactor = 1.0;
                
                var width = Math.Max((maxLineLength / CHARS_PER_FOOT) * scaleFactor, _tagWidth);
                var height = Math.Max(lineCount * BASE_TAG_HEIGHT * scaleFactor, _tagHeight);
                
                // Sanity check - prevent extremely large or small values
                width = Math.Max(0.1, Math.Min(width, 50.0));
                height = Math.Max(0.1, Math.Min(height, 20.0));
                
                return (width, height);
            }
            catch
            {
                // Fallback to defaults
                return (_tagWidth, _tagHeight);
            }
        }

        /// <summary>
        /// Calculate optimal tag placements using Quick Mode (greedy algorithm).
        /// </summary>
        public List<TagPlacement> CalculatePlacements(List<TaggableElement> elements, TagSettings settings)
        {
            var placements = new List<TagPlacement>();
            
            // Sort elements by position (top-left to bottom-right) for consistent placement
            var sortedElements = elements
                .Where(e => !e.IsInGroup || ShouldTagGroupedElement(e)) // #9: Handle grouped elements
                .OrderByDescending(e => e.Center.Y) // Top first
                .ThenBy(e => e.Center.X) // Left first
                .ToList();

            foreach (var element in sortedElements)
            {
                // Skip if already tagged and setting is enabled
                if (settings.SkipTaggedElements && element.HasExistingTag)
                    continue;

                // #8: Handle linear elements (long pipes/ducts)
                if (element.IsLinearElement && element.LengthFeet > MIN_LINEAR_LENGTH_FOR_SPLIT)
                {
                    var segmentPlacements = CreateLinearElementPlacements(element, settings);
                    foreach (var placement in segmentPlacements)
                    {
                        placements.Add(placement);
                        _tagIndex.Add(element.ElementId + 1000000 + placement.SegmentIndex, placement.EstimatedTagBounds);
                    }
                }
                else
                {
                    var placement = FindBestPlacement(element, settings);
                    if (placement != null)
                    {
                        placements.Add(placement);
                        // Add to tag index to prevent future collisions
                        _tagIndex.Add(element.ElementId + 1000000, placement.EstimatedTagBounds);
                    }
                }
            }

            // Optional: Align tags if setting is enabled
            if (settings.AlignTags)
            {
                AlignTagPlacements(placements, settings);
            }

            return placements;
        }
        
        /// <summary>
        /// #9: Determine if a grouped element should be tagged.
        /// Generally, we tag grouped elements unless they're nested copies.
        /// </summary>
        private bool ShouldTagGroupedElement(TaggableElement element)
        {
            // For now, allow tagging grouped elements
            // In future, could check if element is original vs copied instance
            return true;
        }
        
        /// <summary>
        /// #8: Create multiple tag placements for long linear elements (pipes/ducts).
        /// </summary>
        private List<TagPlacement> CreateLinearElementPlacements(TaggableElement element, TagSettings settings)
        {
            var placements = new List<TagPlacement>();
            
            try
            {
                if (element == null || !element.StartPoint.HasValue || !element.EndPoint.HasValue)
                {
                    // Fallback to single tag at center
                    var singlePlacement = FindBestPlacement(element, settings);
                    if (singlePlacement != null) placements.Add(singlePlacement);
                    return placements;
                }
                
                var start = element.StartPoint.Value;
                var end = element.EndPoint.Value;
                var length = element.LengthFeet;
                
                // Guard against invalid length
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length))
                {
                    var singlePlacement = FindBestPlacement(element, settings);
                    if (singlePlacement != null) placements.Add(singlePlacement);
                    return placements;
                }
                
                // Calculate number of segments
                var numSegments = (int)Math.Ceiling(length / LINEAR_SEGMENT_LENGTH);
                numSegments = Math.Max(1, Math.Min(numSegments, 5)); // 1-5 tags per element
                
                // #10: Determine tag rotation based on element angle
                var rotation = GetTagRotationForAngle(element.AngleRadians);
                
                for (int i = 0; i < numSegments; i++)
                {
                    // Calculate position along the line
                    double t = (i + 0.5) / numSegments; // Center of each segment
                    var segmentCenter = new Point2D(
                        start.X + (end.X - start.X) * t,
                        start.Y + (end.Y - start.Y) * t);
                    
                    // Validate coordinates
                    if (double.IsNaN(segmentCenter.X) || double.IsNaN(segmentCenter.Y) ||
                        double.IsInfinity(segmentCenter.X) || double.IsInfinity(segmentCenter.Y))
                    {
                        continue;
                    }
                    
                    // Get dynamic tag size
                    var (tagWidth, tagHeight) = EstimateTagSize(element);
                    
                    // Swap width/height for vertical tags
                    if (rotation == TagRotation.Vertical)
                    {
                        (tagWidth, tagHeight) = (tagHeight, tagWidth);
                    }
                    
                    // Find best position for this segment
                    var candidates = GenerateCandidatePositionsAtPoint(
                        element, segmentCenter, tagWidth, tagHeight, settings, rotation);
                    
                    if (candidates == null || candidates.Count == 0) continue;
                    
                    TagPlacement bestPlacement = null;
                    double bestScore = double.MaxValue;
                    
                    foreach (var candidate in candidates)
                    {
                        if (candidate == null) continue;
                        if (_viewCropBox.HasValue && !_viewCropBox.Value.Contains(candidate.TagLocation))
                            continue;
                        
                        var score = ScorePlacement(candidate, element, settings);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestPlacement = candidate;
                        }
                    }
                    
                    if (bestPlacement != null && bestScore <= MAX_ACCEPTABLE_SCORE)
                    {
                        bestPlacement.Score = bestScore;
                        bestPlacement.SegmentIndex = i;
                        bestPlacement.Rotation = rotation;
                        placements.Add(bestPlacement);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in CreateLinearElementPlacements: {ex.Message}");
                // Fallback to single tag
                try
                {
                    var singlePlacement = FindBestPlacement(element, settings);
                    if (singlePlacement != null) placements.Add(singlePlacement);
                }
                catch { /* Ignore fallback failure */ }
            }
            
            return placements;
        }
        
        /// <summary>
        /// #10: Get tag rotation based on element angle.
        /// Only returns 0° (Horizontal) or 90° (Vertical).
        /// </summary>
        private TagRotation GetTagRotationForAngle(double angleRadians)
        {
            // Normalize angle to 0-PI range (tags read left-to-right or bottom-to-top)
            var normalizedAngle = Math.Abs(angleRadians);
            while (normalizedAngle > Math.PI) normalizedAngle -= Math.PI;
            
            // If angle is closer to vertical (45° to 135°), use vertical tag
            if (normalizedAngle > VERTICAL_ANGLE_THRESHOLD && 
                normalizedAngle < (Math.PI - VERTICAL_ANGLE_THRESHOLD))
            {
                return TagRotation.Vertical;
            }
            
            return TagRotation.Horizontal;
        }
        
        /// <summary>
        /// Generate candidate positions at a specific point (for linear element segments).
        /// </summary>
        private List<TagPlacement> GenerateCandidatePositionsAtPoint(
            TaggableElement element, Point2D center, double tagWidth, double tagHeight,
            TagSettings settings, TagRotation rotation)
        {
            var candidates = new List<TagPlacement>();
            
            // Offset from center for tag placement
            var offset = _leaderLength + Math.Max(tagWidth, tagHeight) / 2;
            
            // #12: Get preferred positions from rules
            var preferredPositions = GetPreferredPositionsFromRule(element);
            
            // Define candidate positions
            var positions = new (TagPosition pos, double offsetX, double offsetY)[]
            {
                (TagPosition.TopRight, offset, offset),
                (TagPosition.TopLeft, -offset, offset),
                (TagPosition.TopCenter, 0, offset),
                (TagPosition.BottomRight, offset, -offset),
                (TagPosition.BottomLeft, -offset, -offset),
                (TagPosition.BottomCenter, 0, -offset),
                (TagPosition.Right, offset, 0),
                (TagPosition.Left, -offset, 0),
                (TagPosition.Center, 0, 0)
            };
            
            foreach (var (pos, offsetX, offsetY) in positions)
            {
                var tagLocation = new Point2D(center.X + offsetX, center.Y + offsetY);
                var leaderEnd = pos == TagPosition.Center ? tagLocation : center;
                var hasLeader = pos != TagPosition.Center;
                
                var tagBounds = new BoundingBox2D(
                    tagLocation.X - tagWidth / 2,
                    tagLocation.Y - tagHeight / 2,
                    tagLocation.X + tagWidth / 2,
                    tagLocation.Y + tagHeight / 2);
                
                candidates.Add(new TagPlacement
                {
                    ElementId = element.ElementId,
                    TagLocation = tagLocation,
                    LeaderEnd = leaderEnd,
                    HasLeader = hasLeader,
                    Position = pos,
                    EstimatedTagBounds = tagBounds,
                    Rotation = rotation
                });
            }
            
            // Sort by preferred positions first
            if (preferredPositions.Count > 0)
            {
                candidates = candidates
                    .OrderBy(c => preferredPositions.IndexOf(c.Position.ToString()) is int idx && idx >= 0 ? idx : 999)
                    .ToList();
            }
            
            return candidates;
        }
        
        /// <summary>
        /// #12: Get preferred tag positions from matched rule.
        /// </summary>
        private List<string> GetPreferredPositionsFromRule(TaggableElement element)
        {
            if (element == null || _ruleEngine == null)
                return new List<string>();
            
            try
            {
                var rule = _ruleEngine.GetBestTaggingRule(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.FamilyName ?? "",
                    null, // viewType - will match any
                    element.SystemClassification ?? "",
                    element.SystemName ?? "");
                
                if (rule?.Actions?.PreferredPositions != null && rule.Actions.PreferredPositions.Count > 0)
                {
                    return new List<string>(rule.Actions.PreferredPositions);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Rule lookup failed: {ex.Message}");
            }
            
            return new List<string>();
        }

        /// <summary>
        /// Find the best placement for a single element's tag.
        /// </summary>
        private TagPlacement FindBestPlacement(TaggableElement element, TagSettings settings)
        {
            var candidates = GenerateCandidatePositions(element, settings);
            TagPlacement bestPlacement = null;
            double bestScore = double.MaxValue;

            foreach (var candidate in candidates)
            {
                // Skip candidates outside view crop box
                if (_viewCropBox.HasValue && !_viewCropBox.Value.Contains(candidate.TagLocation))
                {
                    continue;
                }
                
                var score = ScorePlacement(candidate, element, settings);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPlacement = candidate;
                }
            }

            // Check if best placement is acceptable
            if (bestPlacement != null)
            {
                bestPlacement.Score = bestScore;
                
                // If all positions have too many collisions, skip this element
                if (bestScore > MAX_ACCEPTABLE_SCORE)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Skipping element {element.ElementId}: best score {bestScore} exceeds threshold {MAX_ACCEPTABLE_SCORE}");
                    return null;
                }
            }

            return bestPlacement;
        }

        /// <summary>
        /// Generate candidate positions for a tag around an element.
        /// </summary>
        private List<TagPlacement> GenerateCandidatePositions(TaggableElement element, TagSettings settings)
        {
            var candidates = new List<TagPlacement>();
            var bounds = element.ViewBounds;
            var center = element.Center;
            
            // Get dynamic tag size based on content
            var (tagWidth, tagHeight) = EstimateTagSize(element);
            
            // #10: Determine tag rotation for linear elements
            var rotation = element.IsLinearElement 
                ? GetTagRotationForAngle(element.AngleRadians) 
                : TagRotation.Horizontal;
            
            // Swap width/height for vertical tags
            if (rotation == TagRotation.Vertical)
            {
                (tagWidth, tagHeight) = (tagHeight, tagWidth);
            }
            
            // #12: Get preferred positions from rules
            var preferredPositions = GetPreferredPositionsFromRule(element);

            // Define candidate positions around the element
            var positions = new (TagPosition pos, double offsetX, double offsetY)[]
            {
                // Primary positions (with leader)
                (TagPosition.TopRight, bounds.Width / 2 + _leaderLength + tagWidth / 2, 
                    bounds.Height / 2 + _leaderLength + tagHeight / 2),
                (TagPosition.TopLeft, -(bounds.Width / 2 + _leaderLength + tagWidth / 2), 
                    bounds.Height / 2 + _leaderLength + tagHeight / 2),
                (TagPosition.TopCenter, 0, bounds.Height / 2 + _leaderLength + tagHeight / 2),
                (TagPosition.BottomRight, bounds.Width / 2 + _leaderLength + tagWidth / 2, 
                    -(bounds.Height / 2 + _leaderLength + tagHeight / 2)),
                (TagPosition.BottomLeft, -(bounds.Width / 2 + _leaderLength + tagWidth / 2), 
                    -(bounds.Height / 2 + _leaderLength + tagHeight / 2)),
                (TagPosition.BottomCenter, 0, -(bounds.Height / 2 + _leaderLength + tagHeight / 2)),
                (TagPosition.Right, bounds.Width / 2 + _leaderLength + tagWidth / 2, 0),
                (TagPosition.Left, -(bounds.Width / 2 + _leaderLength + tagWidth / 2), 0),
                // Center (no leader)
                (TagPosition.Center, 0, 0)
            };

            foreach (var (pos, offsetX, offsetY) in positions)
            {
                var tagLocation = new Point2D(center.X + offsetX, center.Y + offsetY);
                var leaderEnd = pos == TagPosition.Center ? tagLocation : center;
                var hasLeader = pos != TagPosition.Center;

                var tagBounds = new BoundingBox2D(
                    tagLocation.X - tagWidth / 2,
                    tagLocation.Y - tagHeight / 2,
                    tagLocation.X + tagWidth / 2,
                    tagLocation.Y + tagHeight / 2);

                candidates.Add(new TagPlacement
                {
                    ElementId = element.ElementId,
                    TagLocation = tagLocation,
                    LeaderEnd = leaderEnd,
                    HasLeader = hasLeader,
                    Position = pos,
                    EstimatedTagBounds = tagBounds,
                    Rotation = rotation
                });
            }
            
            // Sort by preferred positions from rules first
            if (preferredPositions.Count > 0)
            {
                candidates = candidates
                    .OrderBy(c => {
                        var idx = preferredPositions.IndexOf(c.Position.ToString());
                        return idx >= 0 ? idx : 999;
                    })
                    .ToList();
            }

            return candidates;
        }

        /// <summary>
        /// Score a tag placement (lower is better).
        /// </summary>
        private double ScorePlacement(TagPlacement placement, TaggableElement element, TagSettings settings)
        {
            double score = 0;

            // 1. Tag-to-tag collision penalty (highest weight)
            var tagCollisions = _tagIndex.GetCollisions(placement.EstimatedTagBounds);
            score += tagCollisions.Count * COLLISION_PENALTY;

            // 2. Tag-to-element collision (tag overlapping other elements)
            var elementCollisions = _elementIndex.GetCollisions(
                placement.EstimatedTagBounds.Expand(-0.1), // Slight shrink to allow touching
                element.ElementId);
            score += elementCollisions.Count * ELEMENT_COLLISION_PENALTY;
            
            // 3. Tag-to-annotation collision (dimensions, text notes)
            var annotationCollisions = _annotationIndex.GetCollisions(placement.EstimatedTagBounds);
            score += annotationCollisions.Count * ELEMENT_COLLISION_PENALTY;
            
            // 4. Leader collision check (if has leader)
            if (placement.HasLeader)
            {
                var leaderCollisions = CheckLeaderCollisions(placement, element.ElementId);
                score += leaderCollisions * LEADER_COLLISION_PENALTY;
            }

            // 5. Position preference
            score += GetPositionPreferenceScore(placement.Position, settings.PreferredPosition);

            // 6. Leader length penalty
            if (placement.HasLeader)
            {
                var leaderLength = placement.TagLocation.DistanceTo(placement.LeaderEnd);
                score += leaderLength * 2; // Prefer shorter leaders
            }

            // 7. Distance from element center
            var distFromCenter = placement.TagLocation.DistanceTo(element.Center);
            score += distFromCenter * 0.5;

            // 8. Alignment bonus (tags that align with grid get lower score)
            var alignmentScore = GetAlignmentScore(placement.TagLocation);
            score -= alignmentScore * 5; // Subtract to favor aligned positions

            return score;
        }
        
        /// <summary>
        /// Check if leader line collides with elements or tags.
        /// Returns number of collisions.
        /// </summary>
        private int CheckLeaderCollisions(TagPlacement placement, long excludeElementId)
        {
            int collisions = 0;
            
            // Create bounding box for leader line
            var leaderBounds = GetLeaderBounds(placement.TagLocation, placement.LeaderEnd);
            
            // Check against elements (excluding the host element)
            var elementCollisions = _elementIndex.GetCollisions(leaderBounds, excludeElementId);
            collisions += elementCollisions.Count;
            
            // Check against other tags
            var tagCollisions = _tagIndex.GetCollisions(leaderBounds);
            collisions += tagCollisions.Count;
            
            return collisions;
        }
        
        /// <summary>
        /// Get bounding box for a leader line (with small thickness).
        /// </summary>
        private BoundingBox2D GetLeaderBounds(Point2D start, Point2D end)
        {
            const double leaderThickness = 0.05; // Small thickness for collision
            
            var minX = Math.Min(start.X, end.X) - leaderThickness;
            var maxX = Math.Max(start.X, end.X) + leaderThickness;
            var minY = Math.Min(start.Y, end.Y) - leaderThickness;
            var maxY = Math.Max(start.Y, end.Y) + leaderThickness;
            
            return new BoundingBox2D(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// Get score based on position preference (lower is better).
        /// </summary>
        private double GetPositionPreferenceScore(TagPosition actual, TagPosition preferred)
        {
            if (actual == preferred) return 0;
            
            // Define position groups
            var topPositions = new[] { TagPosition.TopRight, TagPosition.TopLeft, TagPosition.TopCenter };
            var bottomPositions = new[] { TagPosition.BottomRight, TagPosition.BottomLeft, TagPosition.BottomCenter };
            var sidePositions = new[] { TagPosition.Left, TagPosition.Right };

            // Same group = lower penalty
            bool actualTop = topPositions.Contains(actual);
            bool preferredTop = topPositions.Contains(preferred);
            bool actualBottom = bottomPositions.Contains(actual);
            bool preferredBottom = bottomPositions.Contains(preferred);
            bool actualSide = sidePositions.Contains(actual);
            bool preferredSide = sidePositions.Contains(preferred);

            if ((actualTop && preferredTop) || (actualBottom && preferredBottom) || 
                (actualSide && preferredSide))
                return 5;

            if (actual == TagPosition.Center) return 15; // Center usually less preferred
            
            return 10;
        }

        /// <summary>
        /// Get alignment score (higher is better - more aligned).
        /// </summary>
        private double GetAlignmentScore(Point2D location)
        {
            // Check alignment with common intervals (e.g., 1 foot grid)
            double gridSize = 1.0;
            var xAlign = 1.0 - Math.Abs((location.X % gridSize) - gridSize / 2) / (gridSize / 2);
            var yAlign = 1.0 - Math.Abs((location.Y % gridSize) - gridSize / 2) / (gridSize / 2);
            return (xAlign + yAlign) / 2;
        }

        /// <summary>
        /// Align tag placements in rows/columns for cleaner appearance.
        /// Only aligns if it doesn't cause new collisions.
        /// </summary>
        private void AlignTagPlacements(List<TagPlacement> placements, TagSettings settings)
        {
            if (placements.Count < 2) return;

            // Group tags by approximate Y position (rows)
            var rowTolerance = _tagHeight * 2;
            var rows = new List<List<TagPlacement>>();

            foreach (var placement in placements.OrderByDescending(p => p.TagLocation.Y))
            {
                var addedToRow = false;
                foreach (var row in rows)
                {
                    if (Math.Abs(row[0].TagLocation.Y - placement.TagLocation.Y) < rowTolerance)
                    {
                        row.Add(placement);
                        addedToRow = true;
                        break;
                    }
                }
                if (!addedToRow)
                {
                    rows.Add(new List<TagPlacement> { placement });
                }
            }

            // Align Y within each row (only if safe)
            foreach (var row in rows)
            {
                if (row.Count < 2) continue;
                
                var avgY = row.Average(p => p.TagLocation.Y);
                
                // Check if alignment would cause collisions
                var safeToAlign = true;
                foreach (var placement in row)
                {
                    var offset = avgY - placement.TagLocation.Y;
                    var newBounds = new BoundingBox2D(
                        placement.EstimatedTagBounds.MinX,
                        placement.EstimatedTagBounds.MinY + offset,
                        placement.EstimatedTagBounds.MaxX,
                        placement.EstimatedTagBounds.MaxY + offset);
                    
                    // Check if new position collides with other placements (not in this row)
                    foreach (var other in placements)
                    {
                        if (row.Contains(other)) continue;
                        if (newBounds.Intersects(other.EstimatedTagBounds))
                        {
                            safeToAlign = false;
                            break;
                        }
                    }
                    if (!safeToAlign) break;
                }
                
                // Only align if safe
                if (safeToAlign)
                {
                    foreach (var placement in row)
                    {
                        var newLocation = new Point2D(placement.TagLocation.X, avgY);
                        var offset = newLocation.Y - placement.TagLocation.Y;
                        
                        placement.TagLocation = newLocation;
                        placement.EstimatedTagBounds = new BoundingBox2D(
                            placement.EstimatedTagBounds.MinX,
                            placement.EstimatedTagBounds.MinY + offset,
                            placement.EstimatedTagBounds.MaxX,
                            placement.EstimatedTagBounds.MaxY + offset);
                    }
                }
            }
        }

        /// <summary>
        /// #11: Resolve collisions by pushing tags apart - OPTIMIZED using spatial index.
        /// </summary>
        public void ResolveCollisions(List<TagPlacement> placements, int maxIterations = 10)
        {
            if (placements == null || placements.Count == 0) return;
            
            try
            {
                // Validate cell size
                var cellSize = Math.Max(_tagWidth, _tagHeight) * 2;
                if (cellSize <= 0 || double.IsNaN(cellSize) || double.IsInfinity(cellSize))
                    cellSize = 3.0;
                
                // Build a temporary spatial index for new placements
                var placementIndex = new SpatialIndex(cellSize);
                
                for (int i = 0; i < placements.Count; i++)
                {
                    var p = placements[i];
                    if (p == null) continue;
                    
                    var id = p.ElementId * 1000 + p.SegmentIndex; // Unique ID per placement
                    placementIndex.Add(id, p.EstimatedTagBounds, p);
                }
                
                for (int iter = 0; iter < maxIterations; iter++)
                {
                    bool hasCollision = false;
                    var processedPairs = new HashSet<(long, long)>();
                    int collisionCount = 0;
                    const int maxCollisionsPerIteration = 1000; // Prevent runaway

                    foreach (var placement in placements)
                    {
                        if (placement == null) continue;
                        if (collisionCount > maxCollisionsPerIteration) break;
                        
                        var id = placement.ElementId * 1000 + placement.SegmentIndex;
                        
                        // Use spatial index to find nearby placements (O(1) average)
                        var expandedBounds = placement.EstimatedTagBounds.Expand(MIN_SPACING);
                        var nearby = placementIndex.Query(expandedBounds);
                        
                        if (nearby == null) continue;
                        
                        foreach (var item in nearby)
                        {
                            if (item == null || item.Id == id) continue; // Skip self
                            
                            // Avoid processing same pair twice
                            var pairKey = id < item.Id ? (id, item.Id) : (item.Id, id);
                            if (processedPairs.Contains(pairKey)) continue;
                            processedPairs.Add(pairKey);
                            
                            var other = item.Data as TagPlacement;
                            if (other == null) continue;
                            
                            if (placement.EstimatedTagBounds.Intersects(other.EstimatedTagBounds))
                            {
                                hasCollision = true;
                                collisionCount++;
                                PushApart(placement, other);
                            }
                        }
                    }
                    
                    // Update spatial index after movements
                    if (hasCollision)
                    {
                        placementIndex.Clear();
                        foreach (var p in placements)
                        {
                            if (p == null) continue;
                            var id = p.ElementId * 1000 + p.SegmentIndex;
                            placementIndex.Add(id, p.EstimatedTagBounds, p);
                        }
                    }
                    
                    if (!hasCollision) break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in ResolveCollisions: {ex.Message}");
                // Continue without collision resolution rather than crash
            }
        }

        private void PushApart(TagPlacement a, TagPlacement b)
        {
            var centerA = a.EstimatedTagBounds.Center;
            var centerB = b.EstimatedTagBounds.Center;

            var dx = centerB.X - centerA.X;
            var dy = centerB.Y - centerA.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < 0.001) // Nearly identical positions
            {
                dx = 1;
                dy = 0;
                dist = 1;
            }

            // Calculate overlap
            var overlapX = (a.EstimatedTagBounds.Width + b.EstimatedTagBounds.Width) / 2 - Math.Abs(dx);
            var overlapY = (a.EstimatedTagBounds.Height + b.EstimatedTagBounds.Height) / 2 - Math.Abs(dy);

            // Push in direction of least resistance
            double pushX, pushY;
            if (overlapX < overlapY)
            {
                pushX = (overlapX / 2 + MIN_SPACING) * Math.Sign(dx);
                pushY = 0;
            }
            else
            {
                pushX = 0;
                pushY = (overlapY / 2 + MIN_SPACING) * Math.Sign(dy);
            }

            // Move tags apart
            MoveTag(a, -pushX, -pushY);
            MoveTag(b, pushX, pushY);
        }

        private void MoveTag(TagPlacement placement, double dx, double dy)
        {
            placement.TagLocation = new Point2D(
                placement.TagLocation.X + dx,
                placement.TagLocation.Y + dy);

            placement.EstimatedTagBounds = new BoundingBox2D(
                placement.EstimatedTagBounds.MinX + dx,
                placement.EstimatedTagBounds.MinY + dy,
                placement.EstimatedTagBounds.MaxX + dx,
                placement.EstimatedTagBounds.MaxY + dy);
        }
    }
}
