using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SmartTag.Models;
using SmartTag.Services;

namespace SmartTag.ML
{
    /// <summary>
    /// Unified placement engine that combines:
    /// 1. Context Analysis - understand element surroundings
    /// 2. KNN Matching - find similar examples from training data
    /// 3. CSP Solver - ensure constraints are satisfied
    /// 
    /// This is the main entry point for ML-enhanced tag placement.
    /// </summary>
    public class PlacementEngine
    {
        private readonly Document _doc;
        private readonly View _view;
        private readonly ContextAnalyzer _contextAnalyzer;
        private readonly FeatureExtractor _featureExtractor;
        private readonly KNNMatcher _knnMatcher;
        private readonly CSPSolver _cspSolver;
        private readonly TagSizeCalibration _calibration;
        private readonly RuleEngine _ruleEngine;
        private readonly TagPositionPatternLoader _patternLoader;
        private readonly TagPlacementRadiusService _radiusService = TagPlacementRadiusService.Instance;

        // Configuration
        private readonly int _knnK;
        private readonly bool _useKNN;
        private readonly bool _useCSP;
        private readonly bool _useRules;
        private readonly bool _usePatterns;

        // Cached values
        private double _tagWidth;
        private double _tagHeight;
        private double _minSpacing;
        private double _leaderLength;
        private double _viewScale;

        public PlacementEngine(Document doc, View view, PlacementEngineOptions options = null)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));

            options ??= new PlacementEngineOptions();
            _knnK = options.KnnK;
            _useKNN = options.UseKNN;
            _useCSP = options.UseCSP;
            _useRules = options.UseRules;
            _usePatterns = options.UsePatterns;

            // Rule engine (repo Rules from Data/Rules/Tagging)
            _ruleEngine = RuleEngine.Instance;
            _ruleEngine.Initialize();

            // Pattern loader (repo Patterns from Data/Patterns/TagPositions)
            _patternLoader = TagPositionPatternLoader.Instance;
            _patternLoader.Initialize();

            // Initialize components
            _contextAnalyzer = new ContextAnalyzer(options.NeighborRadius, options.WallDetectionRadius);
            _featureExtractor = new FeatureExtractor();
            _knnMatcher = new KNNMatcher();
            _cspSolver = new CSPSolver(options.CSPConstraints);

            // Load calibration
            try
            {
                _calibration = new TagSizeCalibration(doc, view);
                _tagWidth = _calibration.BaseTagWidth;
                _tagHeight = _calibration.BaseTagHeight;
                _minSpacing = _calibration.MinSpacing;
                _leaderLength = _calibration.LeaderLength;
                _viewScale = _calibration.ViewScale;
            }
            catch
            {
                // Fallback values
                _viewScale = view.Scale > 0 ? view.Scale : 100;
                var inverseScale = 100.0 / _viewScale;
                _tagWidth = 3.0 * inverseScale;
                _tagHeight = 1.0 * inverseScale;
                _minSpacing = 1.5 * inverseScale;
                _leaderLength = 2.0 * inverseScale;
            }

            // Load KNN training data if enabled
            if (_useKNN && !string.IsNullOrEmpty(options.TrainingDataPath))
            {
                LoadTrainingData(options.TrainingDataPath);
            }
        }

        /// <summary>
        /// Load training data for KNN matching.
        /// </summary>
        public void LoadTrainingData(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    _knnMatcher.LoadTrainingData(path);
                    System.Diagnostics.Debug.WriteLine($"PlacementEngine: Loaded {_knnMatcher.SampleCount} training samples");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlacementEngine: Failed to load training data: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate optimal placements for all elements using ML pipeline.
        /// </summary>
        public List<TagPlacement> CalculatePlacements(
            List<TaggableElement> elements,
            TagSettings settings,
            List<TagPlacement> existingPlacements = null)
        {
            var placements = new List<TagPlacement>();
            existingPlacements ??= new List<TagPlacement>();

            // Sort elements (top-left to bottom-right)
            var sortedElements = elements
                .Where(e => !e.HasExistingTag || !settings.SkipTaggedElements)
                .OrderByDescending(e => e.Center.Y)
                .ThenBy(e => e.Center.X)
                .ToList();

            // Collect element bounds for CSP
            var elementBounds = elements.Select(e => e.ViewBounds).ToList();

            // Get view crop box
            BoundingBox2D? viewCrop = GetViewCropBox();

            foreach (var element in sortedElements)
            {
                var placement = CalculateSinglePlacement(
                    element,
                    elements,
                    placements,
                    existingPlacements,
                    elementBounds,
                    viewCrop,
                    settings);

                if (placement != null)
                {
                    placements.Add(placement);
                }
            }

            // Global alignment optimization
            if (settings.AlignTags && _useCSP)
            {
                _cspSolver.OptimizeGlobalAlignment(placements, _minSpacing);
            }

            return placements;
        }

        /// <summary>
        /// Calculate placement for a single element using the ML pipeline.
        /// </summary>
        private TagPlacement CalculateSinglePlacement(
            TaggableElement element,
            List<TaggableElement> allElements,
            List<TagPlacement> newPlacements,
            List<TagPlacement> existingPlacements,
            List<BoundingBox2D> elementBounds,
            BoundingBox2D? viewCrop,
            TagSettings settings)
        {
            // Step 1: Analyze context
            var context = _contextAnalyzer.Analyze(element, allElements);

            // Step 2: Generate candidate positions
            var candidates = GenerateCandidates(element, context, settings);

            // Step 3: KNN voting (if enabled and have training data)
            TagPositionVote knnVote = null;
            if (_useKNN && _knnMatcher.SampleCount > 0)
            {
                var features = _featureExtractor.ExtractFeatures(element, context);
                var neighbors = _knnMatcher.FindKNearestByCategory(
                    features,
                    element.BuiltInCategoryName ?? "Other",
                    _knnK);
                knnVote = _knnMatcher.Vote(neighbors);

                // Reorder candidates based on KNN vote
                candidates = ReorderByKNNVote(candidates, knnVote);
            }

            // Step 4: CSP solve (if enabled)
            TagPlacement finalPlacement;
            if (_useCSP)
            {
                var allExisting = existingPlacements.Concat(newPlacements).ToList();
                finalPlacement = _cspSolver.Solve(candidates, allExisting, elementBounds, viewCrop);
            }
            else
            {
                // Simple: pick first non-colliding candidate
                finalPlacement = candidates.FirstOrDefault(c =>
                    !HasCollision(c, newPlacements.Concat(existingPlacements).ToList()));

                // Fallback to first candidate if all collide
                finalPlacement ??= candidates.FirstOrDefault();
            }

            // Apply KNN-suggested offset if available
            if (finalPlacement != null && knnVote != null && knnVote.Confidence > 0.5)
            {
                // Fine-tune position based on KNN average offset
                var adjustedX = finalPlacement.TagLocation.X + knnVote.AverageOffsetX * 0.3;
                var adjustedY = finalPlacement.TagLocation.Y + knnVote.AverageOffsetY * 0.3;

                // Update position (with bounds)
                var dx = adjustedX - finalPlacement.TagLocation.X;
                var dy = adjustedY - finalPlacement.TagLocation.Y;

                finalPlacement.TagLocation = new Point2D(adjustedX, adjustedY);
                finalPlacement.EstimatedTagBounds = new BoundingBox2D(
                    finalPlacement.EstimatedTagBounds.MinX + dx,
                    finalPlacement.EstimatedTagBounds.MinY + dy,
                    finalPlacement.EstimatedTagBounds.MaxX + dx,
                    finalPlacement.EstimatedTagBounds.MaxY + dy);
            }

            return finalPlacement;
        }

        /// <summary>
        /// Generate candidate positions based on context analysis and Rules (Data/Rules/Tagging).
        /// </summary>
        private List<TagPlacement> GenerateCandidates(
            TaggableElement element,
            ElementContext context,
            TagSettings settings)
        {
            var candidates = new List<TagPlacement>();
            var center = element.Center;
            var bounds = element.ViewBounds;

            // Get dynamic tag size
            var (tagWidth, tagHeight) = GetTagSize(element);

            // Suggested positions: Rules (repo) first, then context
            var suggestedPositions = GetSuggestedPositions(element, context);

            // Rule-based offset and leader (from Data/Rules/Tagging, then Patterns)
            var ruleOffset = _leaderLength;
            var addLeader = true;
            if (_useRules)
            {
                var rule = _ruleEngine.GetBestTaggingRule(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.FamilyName ?? "",
                    null,
                    element.SystemClassification ?? "",
                    element.SystemName ?? "");
                if (rule?.Actions != null)
                {
                    if (rule.Actions.OffsetDistance > 0)
                        ruleOffset = rule.Actions.OffsetDistance;
                    addLeader = rule.Actions.AddLeader;
                }
            }
            if (_usePatterns && addLeader)
            {
                var patternHint = _patternLoader.GetHint(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.SystemName ?? "",
                    (int)_viewScale);
                addLeader = patternHint.HasLeader;
            }

            // Base offset uses rule or calibration
            var baseOffsetX = bounds.Width / 2 + ruleOffset + tagWidth / 2 + _minSpacing;
            var baseOffsetY = bounds.Height / 2 + ruleOffset + tagHeight / 2 + _minSpacing;

            var compactness = GetCompactnessFactor(context);
            baseOffsetX *= compactness;
            baseOffsetY *= compactness;
            var fallbackRadius = Math.Sqrt(baseOffsetX * baseOffsetX + baseOffsetY * baseOffsetY);
            var radius = GetUsefulRadius(element, context, fallbackRadius, tagWidth, tagHeight);

            // Distance tiers for collision avoidance (tighter by default)
            var distanceMultipliers = new[] { 0.85, 1.0, 1.2, 1.4 };

            foreach (var mult in distanceMultipliers)
            {
                foreach (var position in suggestedPositions)
                {
                    var (offsetX, offsetY) = _contextAnalyzer.CalculateOptimalOffset(
                        element, context, position, tagWidth, tagHeight);

                    offsetX *= mult * compactness;
                    offsetY *= mult * compactness;
                    ClampOffsetToRadius(ref offsetX, ref offsetY, radius);

                    var tagLocation = new Point2D(center.X + offsetX, center.Y + offsetY);
                    var leaderEnd = position == TagPosition.Center ? tagLocation : center;
                    var hasLeader = addLeader && position != TagPosition.Center;

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
                        HasLeader = hasLeader,
                        Position = position,
                        EstimatedTagBounds = tagBounds,
                        DistanceMultiplier = mult
                    });
                }
            }

            // Always add center option (no leader)
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
                DistanceMultiplier = 0
            });

            // Sort by rule preferred positions first, then by pattern positions
            if (_useRules)
            {
                var preferred = GetPreferredPositionsFromRule(element);
                if (preferred.Count > 0)
                {
                    candidates = candidates
                        .OrderBy(c => { var idx = preferred.IndexOf(c.Position.ToString()); return idx >= 0 ? idx : 999; })
                        .ThenBy(c => c.DistanceMultiplier)
                        .ToList();
                }
            }
            else if (_usePatterns)
            {
                var patternHint = _patternLoader.GetHint(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.SystemName ?? "",
                    (int)_viewScale);
                if (patternHint?.Positions != null && patternHint.Positions.Count > 0)
                {
                    candidates = candidates
                        .OrderBy(c => { var idx = patternHint.Positions.IndexOf(c.Position); return idx >= 0 ? idx : 999; })
                        .ThenBy(c => c.DistanceMultiplier)
                        .ToList();
                }
            }

            return candidates;
        }

        private static double GetCompactnessFactor(ElementContext context)
        {
            return context?.Density switch
            {
                DensityLevel.Low => 0.95,
                DensityLevel.High => 0.75,
                _ => 0.85
            };
        }

        private double GetUsefulRadius(
            TaggableElement element,
            ElementContext context,
            double fallbackRadius,
            double tagWidth,
            double tagHeight)
        {
            var radius = _radiusService.GetRadius(element, context, fallbackRadius);
            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius <= 0)
                radius = fallbackRadius;

            var minRadius = Math.Max(_minSpacing * 1.2, Math.Max(tagWidth, tagHeight) * 0.6);
            return Math.Max(radius, minRadius);
        }

        private static void ClampOffsetToRadius(ref double offsetX, ref double offsetY, double radius)
        {
            if (radius <= 0) return;
            var dist = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
            if (dist <= 0.0001 || dist <= radius) return;
            var scale = radius / dist;
            offsetX *= scale;
            offsetY *= scale;
        }

        /// <summary>
        /// Get suggested positions. Priority: Rule (built-in) → Pattern (built-in) → Learned (user export) → context.
        /// </summary>
        private List<TagPosition> GetSuggestedPositions(TaggableElement element, ElementContext context)
        {
            // 1. Rule nội bộ
            var fromRules = GetPreferredPositionsFromRule(element);
            if (fromRules.Count > 0)
            {
                var list = new List<TagPosition>();
                foreach (var s in fromRules)
                {
                    if (Enum.TryParse<TagPosition>(s, out var pos) && pos != TagPosition.Auto)
                        list.Add(pos);
                }
                if (list.Count > 0)
                    return list;
            }

            // 2. Pattern nội bộ (Data/Patterns/TagPositions)
            if (_usePatterns)
            {
                var patternHint = _patternLoader.GetHint(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.SystemName ?? "",
                    (int)_viewScale);
                if (patternHint?.Positions != null && patternHint.Positions.Count > 0)
                {
                    return patternHint.Positions
                        .Where(p => p != TagPosition.Auto)
                        .ToList();
                }
            }

            // 3. Learned (JSON export của người dùng - nếu đã export và cập nhật)
            try
            {
                var learned = LearnedOverridesService.Instance;
                learned.EnsureLoaded();
                var positions = learned.GetPreferredPositions(
                    element.BuiltInCategoryName ?? element.CategoryName ?? "",
                    element.SystemName ?? "");
                if (positions != null && positions.Count > 0)
                {
                    var list = new List<TagPosition>();
                    foreach (var s in positions)
                    {
                        if (Enum.TryParse<TagPosition>(s, out var pos) && pos != TagPosition.Auto)
                            list.Add(pos);
                    }
                    if (list.Count > 0)
                        return list;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlacementEngine learned lookup failed: {ex.Message}");
            }

            return _contextAnalyzer.SuggestPositions(element, context);
        }

        /// <summary>
        /// Get preferred tag positions from repo Rules (Data/Rules/Tagging).
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
                    null,
                    element.SystemClassification ?? "",
                    element.SystemName ?? "");
                if (rule?.Actions?.PreferredPositions != null && rule.Actions.PreferredPositions.Count > 0)
                    return new List<string>(rule.Actions.PreferredPositions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PlacementEngine rule lookup failed: {ex.Message}");
            }
            return new List<string>();
        }

        /// <summary>
        /// Reorder candidates based on KNN voting results.
        /// </summary>
        private List<TagPlacement> ReorderByKNNVote(List<TagPlacement> candidates, TagPositionVote vote)
        {
            if (vote?.PositionScores == null || vote.PositionScores.Count == 0)
                return candidates;

            return candidates
                .OrderByDescending(c => vote.PositionScores.TryGetValue(c.Position, out var score) ? score : 0)
                .ThenBy(c => c.DistanceMultiplier)
                .ToList();
        }

        /// <summary>
        /// Check if a placement collides with existing placements.
        /// </summary>
        private bool HasCollision(TagPlacement candidate, List<TagPlacement> existingPlacements)
        {
            foreach (var existing in existingPlacements)
            {
                if (candidate.EstimatedTagBounds.Intersects(existing.EstimatedTagBounds))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get tag size for element using calibration.
        /// </summary>
        private (double width, double height) GetTagSize(TaggableElement element)
        {
            if (_calibration != null)
            {
                var text = element?.GeneratedTagText ?? element?.SizeString ?? "DN100";
                return _calibration.EstimateTagSize(text);
            }
            return (_tagWidth, _tagHeight);
        }

        /// <summary>
        /// Get view crop box.
        /// </summary>
        private BoundingBox2D? GetViewCropBox()
        {
            try
            {
                if (!_view.CropBoxActive) return null;

                var cropBox = _view.CropBox;
                if (cropBox == null) return null;

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

        #region Public Properties

        public int KNNSampleCount => _knnMatcher?.SampleCount ?? 0;
        public double ViewScale => _viewScale;
        public double TagWidth => _tagWidth;
        public double TagHeight => _tagHeight;
        public double MinSpacing => _minSpacing;

        #endregion
    }

    /// <summary>
    /// Configuration options for PlacementEngine.
    /// </summary>
    public class PlacementEngineOptions
    {
        /// <summary>
        /// Path to training data folder for KNN.
        /// </summary>
        public string TrainingDataPath { get; set; }

        /// <summary>
        /// Number of neighbors for KNN matching.
        /// </summary>
        public int KnnK { get; set; } = 5;

        /// <summary>
        /// Enable KNN-based position suggestion.
        /// </summary>
        public bool UseKNN { get; set; } = true;

        /// <summary>
        /// Enable CSP constraint solver.
        /// </summary>
        public bool UseCSP { get; set; } = true;

        /// <summary>
        /// Radius for neighbor detection (feet).
        /// </summary>
        public double NeighborRadius { get; set; } = 10.0;

        /// <summary>
        /// Radius for wall detection (feet).
        /// </summary>
        public double WallDetectionRadius { get; set; } = 5.0;

        /// <summary>
        /// CSP constraint configuration.
        /// </summary>
        public CSPConstraints CSPConstraints { get; set; }

        /// <summary>
        /// Use repo Rules (Data/Rules/Tagging) for preferred positions and offset/leader.
        /// </summary>
        public bool UseRules { get; set; } = true;

        /// <summary>
        /// Use repo Patterns (Data/Patterns/TagPositions) for position/leader hints when no rule matches.
        /// </summary>
        public bool UsePatterns { get; set; } = true;
    }
}
