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
    /// 
    /// Tag size and spacing are dynamically calibrated using:
    /// 1. Linear regression coefficients from training data (professional drawings)
    /// 2. Runtime Revit API queries for actual tag/text dimensions
    /// </summary>
    public class TagPlacementService
    {
        // =============================================================
        // FALLBACK CONSTANTS (used only if calibration fails)
        // =============================================================
        private const double FALLBACK_TAG_WIDTH = 3.0;
        private const double FALLBACK_TAG_HEIGHT = 1.0;
        private const double FALLBACK_LEADER_LENGTH = 2.0;
        private const double FALLBACK_MIN_SPACING = 1.5;
        
        // Score thresholds
        private const double MAX_ACCEPTABLE_SCORE = 800;
        private const double COLLISION_PENALTY = 500;
        private const double ELEMENT_COLLISION_PENALTY = 150;
        private const double ANNOTATION_COLLISION_PENALTY = 220; // dimensions, text, ClearanceZone - avoid overlap
        private const double LEADER_COLLISION_PENALTY = 50;
        
        // Linear element thresholds
        private const double LINEAR_SEGMENT_LENGTH = 20.0;
        private const double MIN_LINEAR_LENGTH_FOR_SPLIT = 30.0;
        
        // Tag rotation: force one direction (3 o'clock / horizontal)
        private const TagRotation DEFAULT_TAG_ROTATION = TagRotation.Horizontal;
        
        // Rule engine for preferred positions
        private RuleEngine _ruleEngine;
        
        // Calibration service - uses linear regression from training data
        private readonly TagSizeCalibration _calibration;

        private readonly Document _doc;
        private readonly View _view;
        private readonly SpatialIndex _elementIndex;
        private readonly SpatialIndex _tagIndex;
        private readonly SpatialIndex _annotationIndex;
        private readonly double _viewScale;
        private readonly BoundingBox2D? _viewCropBox;

        // Calibrated sizing (from regression + Revit API)
        private double _tagWidth;
        private double _tagHeight;
        private double _leaderLength;
        private double _minSpacing;

        public TagPlacementService(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            
            // =============================================================
            // CALIBRATION: Use linear regression + Revit API
            // =============================================================
            try
            {
                _calibration = new TagSizeCalibration(doc, view);
                
                // Get calibrated values from regression model
                _tagWidth = _calibration.BaseTagWidth;
                _tagHeight = _calibration.BaseTagHeight;
                _leaderLength = _calibration.LeaderLength;
                _minSpacing = _calibration.MinSpacing;
                _viewScale = _calibration.ViewScale;
                
                System.Diagnostics.Debug.WriteLine($"TagPlacementService: Using CALIBRATED values from regression model");
                System.Diagnostics.Debug.WriteLine($"  ViewScale={_viewScale}, TagWidth={_tagWidth:F2}, TagHeight={_tagHeight:F2}, MinSpacing={_minSpacing:F2}, Leader={_leaderLength:F2}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Calibration failed, using fallback: {ex.Message}");
                
                // Fallback to hardcoded values with inverse scale
                _viewScale = view.Scale > 0 ? view.Scale : 100;
                var inverseScaleFactor = 100.0 / _viewScale;
                
                _tagWidth = FALLBACK_TAG_WIDTH * inverseScaleFactor;
                _tagHeight = FALLBACK_TAG_HEIGHT * inverseScaleFactor;
                _leaderLength = FALLBACK_LEADER_LENGTH * inverseScaleFactor;
                _minSpacing = FALLBACK_MIN_SPACING * inverseScaleFactor;
            }
            
            // Adjust cell size based on calibrated spacing
            var cellSize = Math.Max(_minSpacing * 2, 2.0);
            _elementIndex = new SpatialIndex(cellSize);
            _tagIndex = new SpatialIndex(cellSize);
            _annotationIndex = new SpatialIndex(cellSize);
            
            // Get view crop box
            _viewCropBox = GetViewCropBox();
            
            // Initialize rule engine
            _ruleEngine = RuleEngine.Instance;
            _ruleEngine.Initialize();
        }
        
        /// <summary>
        /// Gets the calibrated minimum spacing value.
        /// </summary>
        public double MinSpacing => _minSpacing;
        
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

            if (elements == null) elements = new List<TaggableElement>();
            if (existingTags == null) existingTags = new List<(long, long, BoundingBox2D)>();

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
        /// Estimate tag size using calibrated linear regression model.
        /// Formula: Size = f(CharCount, LineCount, TextHeight, ViewScale)
        /// Returns size in MODEL FEET that accounts for view scale.
        /// </summary>
        private (double width, double height) EstimateTagSize(TaggableElement element)
        {
            try
            {
                // Get generated tag text if available
                var text = element?.GeneratedTagText ?? element?.SizeString ?? "DN100";
                if (string.IsNullOrEmpty(text)) text = "DN100";
                
                // Use calibration service if available (preferred - uses regression model)
                if (_calibration != null)
                {
                    var result = _calibration.EstimateTagSize(text);
                    System.Diagnostics.Debug.WriteLine($"EstimateTagSize (calibrated): text='{text}', width={result.width:F2}, height={result.height:F2}");
                    return result;
                }
                
                // Fallback to simple calculation if calibration unavailable
                var lines = text.Split('\n');
                if (lines == null || lines.Length == 0) 
                    return (_tagWidth, _tagHeight);
                
                var maxLineLength = lines.Max(l => l?.Length ?? 0);
                if (maxLineLength == 0) maxLineLength = 5;
                
                var lineCount = lines.Length;
                
                // Inverse scale factor: 1:50 → 2.0, 1:100 → 1.0, 1:200 → 0.5
                var inverseScaleFactor = 100.0 / _viewScale;
                if (inverseScaleFactor <= 0) inverseScaleFactor = 1.0;
                
                // Simple formula (fallback only)
                var estWidth = Math.Max(maxLineLength * 0.2 * inverseScaleFactor, _tagWidth);
                var estHeight = Math.Max(lineCount * _tagHeight, _tagHeight);
                
                // Add margin for safety (10%)
                estWidth *= 1.1;
                estHeight *= 1.1;
                
                // Sanity check
                estWidth = Math.Max(1.0, Math.Min(estWidth, 50.0));
                estHeight = Math.Max(0.5, Math.Min(estHeight, 25.0));
                
                System.Diagnostics.Debug.WriteLine($"EstimateTagSize (fallback): text='{text}', width={estWidth:F2}, height={estHeight:F2}");
                
                return (estWidth, estHeight);
            }
            catch
            {
                return (_tagWidth, _tagHeight);
            }
        }

        /// <summary>
        /// Calculate optimal tag placements using Quick Mode (greedy algorithm).
        /// </summary>
        public List<TagPlacement> CalculatePlacements(List<TaggableElement> elements, TagSettings settings)
        {
            var placements = new List<TagPlacement>();
            if (elements == null || elements.Count == 0)
                return placements;
            if (settings == null)
                return placements;

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
        /// Re-calculate the best placement for a specific linear segment index.
        /// </summary>
        private TagPlacement FindBestPlacementForSegment(TaggableElement element, TagSettings settings, int segmentIndex)
        {
            try
            {
                if (element == null || !element.StartPoint.HasValue || !element.EndPoint.HasValue)
                    return FindBestPlacement(element, settings);

                var length = element.LengthFeet;
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length))
                    return FindBestPlacement(element, settings);

                var numSegments = (int)Math.Ceiling(length / LINEAR_SEGMENT_LENGTH);
                numSegments = Math.Max(1, Math.Min(numSegments, 5));
                if (segmentIndex < 0 || segmentIndex >= numSegments)
                    segmentIndex = Math.Max(0, Math.Min(segmentIndex, numSegments - 1));

                var rotation = GetTagRotationForAngle(element.AngleRadians);
                var start = element.StartPoint.Value;
                var end = element.EndPoint.Value;
                double t = (segmentIndex + 0.5) / numSegments;
                var segmentCenter = new Point2D(
                    start.X + (end.X - start.X) * t,
                    start.Y + (end.Y - start.Y) * t);

                if (double.IsNaN(segmentCenter.X) || double.IsNaN(segmentCenter.Y) ||
                    double.IsInfinity(segmentCenter.X) || double.IsInfinity(segmentCenter.Y))
                    return FindBestPlacement(element, settings);

                var (tagWidth, tagHeight) = EstimateTagSize(element);
                if (rotation == TagRotation.Vertical)
                    (tagWidth, tagHeight) = (tagHeight, tagWidth);

                var candidates = GenerateCandidatePositionsAtPoint(
                    element, segmentCenter, tagWidth, tagHeight, settings, rotation);

                if (candidates == null || candidates.Count == 0)
                    return null;

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
                    bestPlacement.SegmentIndex = segmentIndex;
                    bestPlacement.Rotation = rotation;
                    return bestPlacement;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FindBestPlacementForSegment failed: {ex.Message}");
            }

            return null;
        }
        
        /// <summary>
        /// #10: Get tag rotation (forced to a single direction).
        /// </summary>
        private TagRotation GetTagRotationForAngle(double angleRadians)
        {
            // Always use horizontal (3 o'clock) orientation
            return DEFAULT_TAG_ROTATION;
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
        /// #12: Get preferred tag positions. Priority: Rule (built-in) → Pattern (built-in) → Learned (user export).
        /// </summary>
        private List<string> GetPreferredPositionsFromRule(TaggableElement element)
        {
            if (element == null || _ruleEngine == null)
                return new List<string>();

            // 1. Rule nội bộ (Data/Rules/Tagging)
            try
            {
                var rule = _ruleEngine.GetBestTaggingRule(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.FamilyName ?? "",
                    null,
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

            // 2. Pattern nội bộ (Data/Patterns/TagPositions)
            try
            {
                var patternLoader = TagPositionPatternLoader.Instance;
                patternLoader.Initialize();
                var viewScale = (int)(_viewScale > 0 ? _viewScale : 100);
                var hint = patternLoader.GetHint(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.SystemName ?? "",
                    viewScale);
                if (hint?.Positions != null && hint.Positions.Count > 0)
                {
                    return hint.Positions
                        .Where(p => p != TagPosition.Auto)
                        .Select(p => p.ToString())
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pattern lookup failed: {ex.Message}");
            }

            // 3. Learned (JSON export của người dùng - nếu đã export và cập nhật)
            try
            {
                var learned = LearnedOverridesService.Instance;
                learned.EnsureLoaded();
                var category = element.BuiltInCategoryName ?? element.CategoryName ?? "";
                var systemName = element.SystemName ?? "";
                var positions = learned.GetPreferredPositions(category, systemName);
                if (positions != null && positions.Count > 0)
                {
                    return positions;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Learned overrides lookup failed: {ex.Message}");
            }

            return new List<string>();
        }
        
        /// <summary>
        /// Get rule-based settings for an element (offset, leader, scoring).
        /// </summary>
        private RuleBasedSettings GetRuleSettings(TaggableElement element)
        {
            var settings = new RuleBasedSettings
            {
                OffsetDistance = _leaderLength,
                AddLeader = true,
                CollisionPenalty = COLLISION_PENALTY,
                PreferenceBonus = 50,
                AlignmentBonus = 20,
                NearEdgeBonus = 10,
                AvoidCategories = new List<string>()
            };
            
            if (element == null || _ruleEngine == null)
                return settings;
            
            try
            {
                var rule = _ruleEngine.GetBestTaggingRule(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.FamilyName ?? "",
                    null,
                    element.SystemClassification ?? "",
                    element.SystemName ?? "");
                
                var ruleMatched = rule?.Actions != null;
                if (ruleMatched)
                {
                    if (rule.Actions.OffsetDistance > 0)
                        settings.OffsetDistance = rule.Actions.OffsetDistance;
                    
                    settings.AddLeader = rule.Actions.AddLeader;
                    
                    if (rule.Actions.AvoidCollisionWith != null)
                        settings.AvoidCategories = rule.Actions.AvoidCollisionWith;
                    
                    if (rule.Actions.GroupAlignment == "AlongCenterline")
                        settings.PreferCenterline = true;
                }

                bool learnedPreferAlignRow = false;
                bool learnedPreferAlignCol = false;

                // Learned (user export): chỉ bù khi rule không chỉ định (ưu tiên rule/pattern nội bộ)
                if (!ruleMatched)
                {
                    try
                    {
                        var learned = LearnedOverridesService.Instance;
                        learned.EnsureLoaded();
                        var category = element.BuiltInCategoryName ?? element.CategoryName ?? "";
                        var systemName = element.SystemName ?? "";
                        var learnedOffset = learned.GetOffsetDistance(category, systemName);
                        var learnedLeader = learned.GetAddLeader(category, systemName);
                        var (preferAlignRow, preferAlignCol) = learned.GetPreferAlignment(category, systemName);
                        if (learnedOffset.HasValue && learnedOffset.Value > 0)
                            settings.OffsetDistance = learnedOffset.Value;
                        if (learnedLeader.HasValue)
                            settings.AddLeader = learnedLeader.Value;
                        learnedPreferAlignRow = preferAlignRow == true;
                        learnedPreferAlignCol = preferAlignCol == true;
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    try
                    {
                        var learned = LearnedOverridesService.Instance;
                        learned.EnsureLoaded();
                        var category = element.BuiltInCategoryName ?? element.CategoryName ?? "";
                        var systemName = element.SystemName ?? "";
                        var (preferAlignRow, preferAlignCol) = learned.GetPreferAlignment(category, systemName);
                        learnedPreferAlignRow = preferAlignRow == true;
                        learnedPreferAlignCol = preferAlignCol == true;
                    }
                    catch { /* ignore */ }
                }
                
                if (rule?.Scoring != null)
                {
                    if (rule.Scoring.CollisionPenalty != 0)
                        settings.CollisionPenalty = Math.Abs(rule.Scoring.CollisionPenalty);
                    
                    if (rule.Scoring.PreferenceBonus > 0)
                        settings.PreferenceBonus = rule.Scoring.PreferenceBonus;
                    
                    if (rule.Scoring.AlignmentBonus > 0)
                        settings.AlignmentBonus = rule.Scoring.AlignmentBonus;
                    
                    if (rule.Scoring.NearEdgeBonus > 0)
                        settings.NearEdgeBonus = rule.Scoring.NearEdgeBonus;
                }

                if (learnedPreferAlignRow || learnedPreferAlignCol)
                {
                    settings.AlignmentBonus = Math.Max(settings.AlignmentBonus, 30); // stronger alignment when learned
                    settings.PreferAlignRow = learnedPreferAlignRow;
                    settings.PreferAlignColumn = learnedPreferAlignCol;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Rule settings lookup failed: {ex.Message}");
            }
            
            return settings;
        }
        
        /// <summary>
        /// Rule-based settings for tag placement.
        /// </summary>
        private class RuleBasedSettings
        {
            public double OffsetDistance { get; set; }
            public bool AddLeader { get; set; }
            public double CollisionPenalty { get; set; }
            public double PreferenceBonus { get; set; }
            public double AlignmentBonus { get; set; }
            public double NearEdgeBonus { get; set; }
            public List<string> AvoidCategories { get; set; }
            public bool PreferCenterline { get; set; }
            public bool PreferAlignRow { get; set; }
            public bool PreferAlignColumn { get; set; }
        }

        /// <summary>
        /// Find the best placement for a single element's tag.
        /// PRIORITIZES non-colliding positions over ANY colliding position.
        /// </summary>
        private TagPlacement FindBestPlacement(TaggableElement element, TagSettings settings)
        {
            var candidates = GenerateCandidatePositions(element, settings);
            
            // Separate into collision-free and colliding candidates
            var collisionFreeCandidates = new List<(TagPlacement placement, double score)>();
            var collidingCandidates = new List<(TagPlacement placement, double score)>();

            foreach (var candidate in candidates)
            {
                // Skip candidates outside view crop box
                if (_viewCropBox.HasValue && !_viewCropBox.Value.Contains(candidate.TagLocation))
                {
                    continue;
                }
                
                // Check if this candidate has ANY tag collision
                var expandedBounds = candidate.EstimatedTagBounds.Expand(_minSpacing * 0.5);
                var hasTagCollision = _tagIndex.GetCollisions(expandedBounds).Count > 0;
                
                var score = ScorePlacement(candidate, element, settings);
                
                if (hasTagCollision)
                {
                    collidingCandidates.Add((candidate, score));
                }
                else
                {
                    collisionFreeCandidates.Add((candidate, score));
                }
            }
            
            // PREFER collision-free candidates
            TagPlacement bestPlacement = null;
            double bestScore = double.MaxValue;
            
            // First, try collision-free candidates
            if (collisionFreeCandidates.Count > 0)
            {
                var best = collisionFreeCandidates.OrderBy(c => c.score).First();
                bestPlacement = best.placement;
                bestScore = best.score;
                System.Diagnostics.Debug.WriteLine($"Element {element.ElementId}: Found collision-free placement at {bestPlacement.Position} with score {bestScore}");
            }
            // Only if NO collision-free option exists, use best colliding option
            else if (collidingCandidates.Count > 0)
            {
                var best = collidingCandidates.OrderBy(c => c.score).First();
                bestPlacement = best.placement;
                bestScore = best.score;
                System.Diagnostics.Debug.WriteLine($"Element {element.ElementId}: No collision-free position, using {bestPlacement.Position} with collisions, score {bestScore}");
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
        /// Uses rule-based settings and multiple distance tiers for better collision avoidance.
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
            
            // Get rule-based settings
            var ruleSettings = GetRuleSettings(element);

            // Use rule-based offset distance if available
            var ruleOffset = ruleSettings.OffsetDistance > 0 ? ruleSettings.OffsetDistance : _leaderLength;
            
            // Base offset calculation
            var baseOffsetX = bounds.Width / 2 + ruleOffset + tagWidth / 2 + _minSpacing;
            var baseOffsetY = bounds.Height / 2 + ruleOffset + tagHeight / 2 + _minSpacing;

            // FOR LINEAR ELEMENTS: Use special positioning along centerline
            if (element.IsLinearElement && ruleSettings.PreferCenterline)
            {
                // Linear elements (pipes/ducts) - tag along centerline with no leader
                candidates.Add(new TagPlacement
                {
                    ElementId = element.ElementId,
                    TagLocation = center,
                    LeaderEnd = center,
                    HasLeader = false, // No leader for linear elements (per rules)
                    Position = TagPosition.Center,
                    EstimatedTagBounds = new BoundingBox2D(
                        center.X - tagWidth / 2 - _minSpacing,
                        center.Y - tagHeight / 2 - _minSpacing,
                        center.X + tagWidth / 2 + _minSpacing,
                        center.Y + tagHeight / 2 + _minSpacing),
                    Rotation = rotation,
                    DistanceMultiplier = 0
                });
                
                // Also add offset positions along the element direction for collision fallback
                if (element.StartPoint.HasValue && element.EndPoint.HasValue)
                {
                    var start = element.StartPoint.Value;
                    var end = element.EndPoint.Value;
                    var dir = new Point2D(end.X - start.X, end.Y - start.Y);
                    var length = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
                    if (length > 0.01)
                    {
                        dir = new Point2D(dir.X / length, dir.Y / length);
                        
                        // Perpendicular offset positions (above/below the line)
                        var perpX = -dir.Y;
                        var perpY = dir.X;
                        var perpOffset = tagHeight + _minSpacing * 2;
                        
                        // Position above centerline
                        var abovePos = new Point2D(center.X + perpX * perpOffset, center.Y + perpY * perpOffset);
                        candidates.Add(new TagPlacement
                        {
                            ElementId = element.ElementId,
                            TagLocation = abovePos,
                            LeaderEnd = center,
                            HasLeader = ruleSettings.AddLeader,
                            Position = TagPosition.TopCenter,
                            EstimatedTagBounds = new BoundingBox2D(
                                abovePos.X - tagWidth / 2 - _minSpacing,
                                abovePos.Y - tagHeight / 2 - _minSpacing,
                                abovePos.X + tagWidth / 2 + _minSpacing,
                                abovePos.Y + tagHeight / 2 + _minSpacing),
                            Rotation = rotation,
                            DistanceMultiplier = 1.0
                        });
                        
                        // Position below centerline
                        var belowPos = new Point2D(center.X - perpX * perpOffset, center.Y - perpY * perpOffset);
                        candidates.Add(new TagPlacement
                        {
                            ElementId = element.ElementId,
                            TagLocation = belowPos,
                            LeaderEnd = center,
                            HasLeader = ruleSettings.AddLeader,
                            Position = TagPosition.BottomCenter,
                            EstimatedTagBounds = new BoundingBox2D(
                                belowPos.X - tagWidth / 2 - _minSpacing,
                                belowPos.Y - tagHeight / 2 - _minSpacing,
                                belowPos.X + tagWidth / 2 + _minSpacing,
                                belowPos.Y + tagHeight / 2 + _minSpacing),
                            Rotation = rotation,
                            DistanceMultiplier = 1.0
                        });
                    }
                }
            }

            // Define positions at MULTIPLE distance tiers for better collision avoidance
            var distanceMultipliers = new double[] { 1.0, 1.5, 2.0, 2.5 }; // 4 tiers of distance
            
            foreach (var mult in distanceMultipliers)
            {
                var offsetX = baseOffsetX * mult;
                var offsetY = baseOffsetY * mult;
                
                var positions = new (TagPosition pos, double ox, double oy)[]
                {
                    // 8 cardinal + diagonal positions
                    (TagPosition.TopRight, offsetX, offsetY),
                    (TagPosition.TopLeft, -offsetX, offsetY),
                    (TagPosition.TopCenter, 0, offsetY),
                    (TagPosition.BottomRight, offsetX, -offsetY),
                    (TagPosition.BottomLeft, -offsetX, -offsetY),
                    (TagPosition.BottomCenter, 0, -offsetY),
                    (TagPosition.Right, offsetX, 0),
                    (TagPosition.Left, -offsetX, 0),
                };

                foreach (var (pos, ox, oy) in positions)
                {
                    var tagLocation = new Point2D(center.X + ox, center.Y + oy);
                    var leaderEnd = center;

                    var tagBounds = new BoundingBox2D(
                        tagLocation.X - tagWidth / 2 - _minSpacing / 2,
                        tagLocation.Y - tagHeight / 2 - _minSpacing / 2,
                        tagLocation.X + tagWidth / 2 + _minSpacing / 2,
                        tagLocation.Y + tagHeight / 2 + _minSpacing / 2);

                    candidates.Add(new TagPlacement
                    {
                        ElementId = element.ElementId,
                        TagLocation = tagLocation,
                        LeaderEnd = leaderEnd,
                        HasLeader = ruleSettings.AddLeader,
                        Position = pos,
                        EstimatedTagBounds = tagBounds,
                        Rotation = rotation,
                        DistanceMultiplier = mult // Track which tier
                    });
                }
            }
            
            // Add center position (no leader) if not already added for linear elements
            if (!element.IsLinearElement || !ruleSettings.PreferCenterline)
            {
                var centerBounds = new BoundingBox2D(
                    center.X - tagWidth / 2 - _minSpacing / 2,
                    center.Y - tagHeight / 2 - _minSpacing / 2,
                    center.X + tagWidth / 2 + _minSpacing / 2,
                    center.Y + tagHeight / 2 + _minSpacing / 2);
                
                candidates.Add(new TagPlacement
                {
                    ElementId = element.ElementId,
                    TagLocation = center,
                    LeaderEnd = center,
                    HasLeader = false,
                    Position = TagPosition.Center,
                    EstimatedTagBounds = centerBounds,
                    Rotation = rotation,
                    DistanceMultiplier = 0
                });
            }
            
            // Sort by preferred positions from rules first, then by distance (prefer closer)
            candidates = candidates
                .OrderBy(c => {
                    var idx = preferredPositions.IndexOf(c.Position.ToString());
                    return idx >= 0 ? idx : 999;
                })
                .ThenBy(c => c.DistanceMultiplier) // Prefer closer positions
                .ToList();

            return candidates;
        }

        /// <summary>
        /// Score a tag placement (lower is better).
        /// Uses rule-based scoring and heavily penalizes collisions to prevent tag overlap.
        /// </summary>
        private double ScorePlacement(TagPlacement placement, TaggableElement element, TagSettings settings)
        {
            double score = 0;
            
            // Get rule-based scoring weights
            var ruleSettings = GetRuleSettings(element);

            // 1. Tag-to-tag collision penalty (HIGHEST weight - prevent overlap)
            // Use expanded bounds for better collision detection
            var expandedBounds = placement.EstimatedTagBounds.Expand(_minSpacing);
            var tagCollisions = _tagIndex.GetCollisions(expandedBounds);
            if (tagCollisions.Count > 0)
            {
                // VERY HIGH penalty - make this almost impossible to choose
                // Each collision adds exponentially more penalty
                score += ruleSettings.CollisionPenalty * Math.Pow(3, tagCollisions.Count);
                
                // Debug logging
                System.Diagnostics.Debug.WriteLine($"Tag collision for element {element.ElementId}: {tagCollisions.Count} collisions, penalty = {score}");
            }

            // 2. Check overlap severity (how much area overlaps)
            foreach (var collision in tagCollisions)
            {
                var overlapArea = GetOverlapArea(placement.EstimatedTagBounds, collision.Bounds);
                if (overlapArea > 0)
                {
                    // VERY HIGH penalty proportional to overlap area
                    score += overlapArea * 200;
                }
            }

            // 3. Tag-to-element collision (tag overlapping other elements)
            var elementCollisions = _elementIndex.GetCollisions(
                placement.EstimatedTagBounds.Expand(-0.1), // Slight shrink to allow touching
                element.ElementId);
            score += elementCollisions.Count * ELEMENT_COLLISION_PENALTY;
            
            // 3b. Check collision with categories specified in rules to avoid
            if (ruleSettings.AvoidCategories.Count > 0)
            {
                foreach (var collision in elementCollisions)
                {
                    if (collision.Data is TaggableElement collidingElement)
                    {
                        if (ruleSettings.AvoidCategories.Contains(collidingElement.BuiltInCategoryName))
                        {
                            score += ELEMENT_COLLISION_PENALTY * 2; // Extra penalty for rule-specified categories
                        }
                    }
                }
            }
            
            // 4. Tag-to-annotation collision (dimensions, text notes, ClearanceZone) - strong penalty to avoid overlap
            var annotationCollisions = _annotationIndex.GetCollisions(placement.EstimatedTagBounds);
            score += annotationCollisions.Count * ANNOTATION_COLLISION_PENALTY;
            
            // 5. Leader collision check (if has leader)
            if (placement.HasLeader)
            {
                var leaderCollisions = CheckLeaderCollisions(placement, element.ElementId);
                score += leaderCollisions * LEADER_COLLISION_PENALTY;
            }

            // 6. Position preference - use rule-based bonus
            var preferenceScore = GetPositionPreferenceScore(placement.Position, settings.PreferredPosition);
            score += preferenceScore;
            
            // 6b. Bonus for rule-preferred positions
            var preferredPositions = GetPreferredPositionsFromRule(element);
            if (preferredPositions.Contains(placement.Position.ToString()))
            {
                score -= ruleSettings.PreferenceBonus; // Lower score = better
            }

            // 7. Leader length penalty (but not too strong - collision avoidance is more important)
            if (placement.HasLeader)
            {
                var leaderLength = placement.TagLocation.DistanceTo(placement.LeaderEnd);
                score += leaderLength * 1; // Reduced weight
            }
            else if (ruleSettings.PreferCenterline && element.IsLinearElement)
            {
                // Bonus for no-leader tags on linear elements (per rule)
                score -= 20;
            }

            // 8. Distance tier penalty (prefer closer positions if no collision)
            if (tagCollisions.Count == 0)
            {
                score += placement.DistanceMultiplier * 5; // Small penalty for farther positions
            }
            else
            {
                // If there's collision, don't penalize farther positions as much
                score += placement.DistanceMultiplier * 1;
            }

            // 9. Alignment bonus (tags that align with grid get lower score)
            var alignmentScore = GetAlignmentScore(placement.TagLocation);
            score -= alignmentScore * ruleSettings.AlignmentBonus / 10;

            // 9b. Column alignment with other tags (bản mẫu: EA/SA stacked - same X)
            var columnAlignTolerance = ruleSettings.PreferAlignColumn ? 2.0 : 1.5; // feet
            var columnBand = new BoundingBox2D(
                placement.TagLocation.X - columnAlignTolerance, placement.EstimatedTagBounds.MinY - 5,
                placement.TagLocation.X + columnAlignTolerance, placement.EstimatedTagBounds.MaxY + 5);
            var nearbyInX = _tagIndex.Query(columnBand);
            foreach (var item in nearbyInX)
            {
                if (item.Bounds.Intersects(placement.EstimatedTagBounds)) continue; // exclude overlapping
                if (Math.Abs(item.Bounds.Center.X - placement.TagLocation.X) <= columnAlignTolerance)
                {
                    var columnAlignBonus = ruleSettings.AlignmentBonus / 2;
                    if (ruleSettings.PreferAlignColumn)
                        columnAlignBonus = ruleSettings.AlignmentBonus;
                    score -= columnAlignBonus; // stronger bonus for stacking in column
                    break;
                }
            }
            
            // 10. Near-edge bonus for equipment (per rule)
            if (ruleSettings.NearEdgeBonus > 0 && !element.IsLinearElement)
            {
                // Bonus if tag is near element edge rather than far away
                var distFromCenter = placement.TagLocation.DistanceTo(element.Center);
                var maxDist = Math.Max(element.ViewBounds.Width, element.ViewBounds.Height) * 2;
                if (distFromCenter < maxDist)
                {
                    score -= ruleSettings.NearEdgeBonus;
                }
            }

            return score;
        }
        
        /// <summary>
        /// Calculate overlap area between two bounding boxes.
        /// </summary>
        private double GetOverlapArea(BoundingBox2D a, BoundingBox2D b)
        {
            var overlapMinX = Math.Max(a.MinX, b.MinX);
            var overlapMaxX = Math.Min(a.MaxX, b.MaxX);
            var overlapMinY = Math.Max(a.MinY, b.MinY);
            var overlapMaxY = Math.Min(a.MaxY, b.MaxY);
            
            if (overlapMinX >= overlapMaxX || overlapMinY >= overlapMaxY)
                return 0;
            
            return (overlapMaxX - overlapMinX) * (overlapMaxY - overlapMinY);
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

            // Column alignment (bản mẫu: EA/SA stacked vertically - same X, different Y)
            var colTolerance = _tagWidth * 2;
            var columns = new List<List<TagPlacement>>();
            foreach (var placement in placements.OrderBy(p => p.TagLocation.X))
            {
                var addedToCol = false;
                foreach (var col in columns)
                {
                    if (Math.Abs(col[0].TagLocation.X - placement.TagLocation.X) < colTolerance)
                    {
                        col.Add(placement);
                        addedToCol = true;
                        break;
                    }
                }
                if (!addedToCol)
                    columns.Add(new List<TagPlacement> { placement });
            }

            foreach (var col in columns)
            {
                if (col.Count < 2) continue;
                var avgX = col.Average(p => p.TagLocation.X);
                var safeToAlign = true;
                foreach (var placement in col)
                {
                    var offset = avgX - placement.TagLocation.X;
                    var newBounds = new BoundingBox2D(
                        placement.EstimatedTagBounds.MinX + offset,
                        placement.EstimatedTagBounds.MinY,
                        placement.EstimatedTagBounds.MaxX + offset,
                        placement.EstimatedTagBounds.MaxY);
                    foreach (var other in placements)
                    {
                        if (col.Contains(other)) continue;
                        if (newBounds.Intersects(other.EstimatedTagBounds)) { safeToAlign = false; break; }
                    }
                    if (!safeToAlign) break;
                }
                if (safeToAlign)
                {
                    foreach (var placement in col)
                    {
                        var offset = avgX - placement.TagLocation.X;
                        placement.TagLocation = new Point2D(avgX, placement.TagLocation.Y);
                        placement.EstimatedTagBounds = new BoundingBox2D(
                            placement.EstimatedTagBounds.MinX + offset,
                            placement.EstimatedTagBounds.MinY,
                            placement.EstimatedTagBounds.MaxX + offset,
                            placement.EstimatedTagBounds.MaxY);
                    }
                }
            }
        }

        /// <summary>
        /// #11: Resolve collisions by pushing tags apart - OPTIMIZED using spatial index.
        /// First pushes tags away from annotations (dimensions, text, ClearanceZone), then tag-vs-tag.
        /// </summary>
        public void ResolveCollisions(List<TagPlacement> placements, int maxIterations = 20) // Increased iterations
        {
            if (placements == null || placements.Count == 0) return;
            
            try
            {
                // 1) Push away from annotations (ClearanceZone, dimensions, text) so tags don't overlap
                const int maxAnnotationPushIter = 5;
                for (int iter = 0; iter < maxAnnotationPushIter; iter++)
                {
                    bool anyPushed = false;
                    foreach (var placement in placements)
                    {
                        if (placement == null) continue;
                        var hits = _annotationIndex.GetCollisions(placement.EstimatedTagBounds);
                        foreach (var hit in hits)
                        {
                            if (PushAwayFromAnnotation(placement, hit.Bounds))
                                anyPushed = true;
                        }
                    }
                    if (!anyPushed) break;
                }

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
                        var expandedBounds = placement.EstimatedTagBounds.Expand(_minSpacing);
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
                // Push in a diagonal direction to spread out - use larger displacement
                dx = _minSpacing * 2;
                dy = _minSpacing * 2;
                dist = Math.Sqrt(dx * dx + dy * dy);
            }

            // Normalize direction
            var nx = dx / dist;
            var ny = dy / dist;

            // Calculate the REQUIRED separation distance (half-widths + half-heights + spacing)
            var requiredSepX = (a.EstimatedTagBounds.Width + b.EstimatedTagBounds.Width) / 2 + _minSpacing;
            var requiredSepY = (a.EstimatedTagBounds.Height + b.EstimatedTagBounds.Height) / 2 + _minSpacing;
            
            // Calculate actual separation
            var actualSepX = Math.Abs(dx);
            var actualSepY = Math.Abs(dy);
            
            // Calculate overlap (positive = overlapping)
            var overlapX = requiredSepX - actualSepX;
            var overlapY = requiredSepY - actualSepY;
            
            // If no overlap in both directions, no need to push
            if (overlapX <= 0 && overlapY <= 0) return;

            // Calculate push distance - push MORE than just the overlap to create clearance
            double pushDist;
            if (overlapX > 0 && overlapY > 0)
            {
                // Full overlap - push along the direction that needs less movement
                // But ensure we move ENOUGH to clear
                var pushX = overlapX / 2 + _minSpacing;
                var pushY = overlapY / 2 + _minSpacing;
                
                // Choose direction that creates most separation
                if (overlapX < overlapY)
                {
                    // Push horizontally
                    nx = Math.Sign(dx) != 0 ? Math.Sign(dx) : 1;
                    ny = 0;
                    pushDist = pushX;
                }
                else
                {
                    // Push vertically
                    nx = 0;
                    ny = Math.Sign(dy) != 0 ? Math.Sign(dy) : 1;
                    pushDist = pushY;
                }
            }
            else if (overlapX > 0)
            {
                // Only horizontal overlap - push horizontally
                pushDist = overlapX / 2 + _minSpacing;
                ny = 0;
                nx = Math.Sign(dx) != 0 ? Math.Sign(dx) : 1;
            }
            else
            {
                // Only vertical overlap - push vertically
                pushDist = overlapY / 2 + _minSpacing;
                nx = 0;
                ny = Math.Sign(dy) != 0 ? Math.Sign(dy) : 1;
            }

            // Apply LARGER push to ensure clearance
            var pushMultiplier = 1.5; // Push 50% more than needed
            pushDist *= pushMultiplier;

            // Move tags apart in opposite directions
            MoveTag(a, -nx * pushDist, -ny * pushDist);
            MoveTag(b, nx * pushDist, ny * pushDist);
            
            System.Diagnostics.Debug.WriteLine($"PushApart: Moved tags {a.ElementId} and {b.ElementId} apart by {pushDist:F2} in direction ({nx:F2}, {ny:F2})");
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

        /// <summary>
        /// Push placement away from an annotation/clearance box so it no longer overlaps. Returns true if moved.
        /// </summary>
        private bool PushAwayFromAnnotation(TagPlacement placement, BoundingBox2D annotationBounds)
        {
            if (!placement.EstimatedTagBounds.Intersects(annotationBounds)) return false;
            var tagCenter = placement.EstimatedTagBounds.Center;
            var annCenter = annotationBounds.Center;
            var dx = tagCenter.X - annCenter.X;
            var dy = tagCenter.Y - annCenter.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001)
            {
                dx = _minSpacing; dy = _minSpacing;
                len = Math.Sqrt(dx * dx + dy * dy);
            }
            var nx = dx / len;
            var ny = dy / len;
            // Minimum push to clear overlap (half tag width/height + half annotation + margin)
            var pushDist = (placement.EstimatedTagBounds.Width + placement.EstimatedTagBounds.Height) / 4
                + (annotationBounds.Width + annotationBounds.Height) / 4 + _minSpacing;
            pushDist = Math.Max(pushDist, _minSpacing * 2);
            MoveTag(placement, nx * pushDist, ny * pushDist);
            return true;
        }

        /// <summary>
        /// Refinement loop: scan newly placed tags that still overlap, re-place or re-run collision/alignment.
        /// Run after initial ResolveCollisions. Repeats: resolve + align → detect overlap → re-place overlapping → until clean or max iterations.
        /// </summary>
        public void RefinePlacementsIterative(List<TagPlacement> placements, List<TaggableElement> elements, TagSettings settings, int maxRefineIterations = 3)
        {
            if (placements == null || placements.Count == 0 || elements == null || settings == null || maxRefineIterations <= 0)
                return;

            var elementById = new Dictionary<long, TaggableElement>();
            foreach (var e in elements)
            {
                if (e == null) continue;
                if (!elementById.ContainsKey(e.ElementId))
                    elementById[e.ElementId] = e;
            }

            for (int refineIter = 0; refineIter < maxRefineIterations; refineIter++)
            {
                // 1) Resolve collisions and alignment
                ResolveCollisions(placements);
                if (settings.AlignTags)
                    AlignTagPlacements(placements, settings);

                // 2) Ensure _tagIndex reflects current placements (in case we replaced some)
                SyncTagIndexFromPlacements(placements);

                // 3) Find placements that still overlap (tag or annotation)
                var overlappingIndices = new List<int>();
                for (int i = 0; i < placements.Count; i++)
                {
                    var p = placements[i];
                    if (p == null) continue;
                    var tagId = p.ElementId * 1000L + p.SegmentIndex;
                    var tagCollisions = _tagIndex.GetCollisions(p.EstimatedTagBounds, tagId);
                    var annCollisions = _annotationIndex.GetCollisions(p.EstimatedTagBounds);
                    if (tagCollisions.Count > 0 || annCollisions.Count > 0)
                        overlappingIndices.Add(i);
                }

                if (overlappingIndices.Count == 0)
                    break;

                // 4) Re-place only single-placement elements (skip linear multi-segment)
                foreach (var index in overlappingIndices)
                {
                    var placement = placements[index];
                    if (placement == null) continue;

                    int sameElementCount = 0;
                    for (int j = 0; j < placements.Count; j++)
                    {
                        if (placements[j] != null && placements[j].ElementId == placement.ElementId)
                            sameElementCount++;
                    }
                    if (!elementById.TryGetValue(placement.ElementId, out var element))
                        continue;

                    var tagId = placement.ElementId * 1000L + placement.SegmentIndex;
                    _tagIndex.Remove(tagId);

                    TagPlacement newPlacement = null;
                    if (sameElementCount == 1)
                    {
                        newPlacement = FindBestPlacement(element, settings);
                    }
                    else if (element.IsLinearElement)
                    {
                        // Re-place only the affected segment for linear elements
                        newPlacement = FindBestPlacementForSegment(element, settings, placement.SegmentIndex);
                    }

                    if (newPlacement != null)
                    {
                        newPlacement.SegmentIndex = placement.SegmentIndex;
                        placements[index] = newPlacement;
                        _tagIndex.Add(tagId, newPlacement.EstimatedTagBounds);
                    }
                    else
                    {
                        _tagIndex.Add(tagId, placement.EstimatedTagBounds);
                    }
                }
            }
        }

        /// <summary>
        /// Update _tagIndex so placement IDs point to current bounds (after ResolveCollisions/Align changed positions).
        /// </summary>
        private void SyncTagIndexFromPlacements(List<TagPlacement> placements)
        {
            if (placements == null) return;
            foreach (var p in placements)
            {
                if (p == null) continue;
                var id = p.ElementId * 1000L + p.SegmentIndex;
                _tagIndex.Remove(id);
                _tagIndex.Add(id, p.EstimatedTagBounds);
            }
        }
    }
}
