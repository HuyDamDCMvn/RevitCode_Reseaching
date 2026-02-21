using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using SmartTag.ML;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Exports training data from professionally tagged Revit views.
    /// Used to collect ground truth data for ML model training.
    /// </summary>
    public class TrainingDataExporter
    {
        private readonly Document _doc;
        private readonly View _view;
        private readonly ContextAnalyzer _contextAnalyzer;

        public TrainingDataExporter(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _contextAnalyzer = new ContextAnalyzer();
        }

        /// <summary>
        /// Export training data from current view to JSON.
        /// </summary>
        public TrainingExportResult Export(string outputPath = null)
        {
            var result = new TrainingExportResult
            {
                ViewName = _view.Name,
                ViewScale = (int)_view.Scale,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // 1. Collect all tagged elements
                var taggedPairs = CollectTaggedElements();
                result.TotalElements = taggedPairs.Count;

                // 2. Collect all elements for context analysis
                var allElements = CollectAllTaggableElements();
                _contextAnalyzer.PrepareIndex(allElements);

                // 3. Collect all tag locations (for alignment-between-tags detection)
                var allTagLocations = new List<Point2D>();
                foreach (var (_, tag) in taggedPairs)
                {
                    try
                    {
                        var tagHead = tag.TagHeadPosition;
                        allTagLocations.Add(ConvertToViewCoordinates(tagHead));
                    }
                    catch { /* skip */ }
                }

                // 4. Build training samples (alignment = tag vs other tags)
                var samples = new List<TrainingSample>();
                for (int i = 0; i < taggedPairs.Count; i++)
                {
                    var (element, tag) = taggedPairs[i];
                    try
                    {
                        var sample = BuildTrainingSample(element, tag, allElements, allTagLocations, i);
                        if (sample != null)
                            samples.Add(sample);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error building sample for element {element.Id}: {ex.Message}");
                        result.Errors.Add($"Element {element.Id}: {ex.Message}");
                    }
                }

                result.ExportedSamples = samples.Count;

                // 4. Build output file
                var trainingData = new TrainingDataFile
                {
                    Version = "1.0",
                    Source = new TrainingSource
                    {
                        Project = _doc.Title ?? "Unknown",
                        Discipline = GuessDiscipline(samples),
                        Drawings = new List<string> { _view.Name },
                        ViewScale = (int)_view.Scale,
                        AnnotatedBy = "TrainingDataExporter",
                        AnnotatedDate = DateTime.UtcNow.ToString("yyyy-MM-dd")
                    },
                    Samples = samples
                };

                // 5. Save to file if path provided
                if (!string.IsNullOrEmpty(outputPath))
                {
                    var json = JsonSerializer.Serialize(trainingData, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });

                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                    File.WriteAllText(outputPath, json);
                    result.OutputPath = outputPath;
                }

                result.TrainingData = trainingData;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Export failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Collect all elements that have tags in the current view.
        /// </summary>
        private List<(Element element, IndependentTag tag)> CollectTaggedElements()
        {
            var pairs = new List<(Element, IndependentTag)>();

            // Get all independent tags in the view
            var tags = new FilteredElementCollector(_doc, _view.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .ToList();

            foreach (var tag in tags)
            {
                try
                {
                    // Get tagged element ID - API differs between Revit versions
                    // Revit 2025+: GetTaggedLocalElementIds()
                    // Revit 2024-: TaggedLocalElementId
                    ElementId elementId = ElementId.InvalidElementId;
                    
                    try
                    {
                        // Try Revit 2025+ API first
                        var taggedIds = tag.GetTaggedLocalElementIds();
                        if (taggedIds != null && taggedIds.Count > 0)
                        {
                            elementId = taggedIds.First();
                        }
                    }
                    catch
                    {
                        // Fallback - use reflection for older API
                        var prop = typeof(IndependentTag).GetProperty("TaggedLocalElementId");
                        if (prop != null)
                        {
                            elementId = prop.GetValue(tag) as ElementId ?? ElementId.InvalidElementId;
                        }
                    }
                    
                    if (elementId == ElementId.InvalidElementId)
                        continue;
                    
                    var element = _doc.GetElement(elementId);
                    
                    if (element != null && IsSupportedCategory(element.Category))
                    {
                        pairs.Add((element, tag));
                    }
                }
                catch
                {
                    // Skip invalid tags
                }
            }

            return pairs;
        }

        /// <summary>
        /// Collect all taggable elements for context analysis.
        /// </summary>
        private List<TaggableElement> CollectAllTaggableElements()
        {
            var elements = new List<TaggableElement>();

            var categories = new[]
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
                BuiltInCategory.OST_DuctTerminal
            };

            var filters = categories
                .Select(c => new ElementCategoryFilter(c))
                .ToList();

            var combinedFilter = new LogicalOrFilter(filters.Cast<ElementFilter>().ToList());

            var revitElements = new FilteredElementCollector(_doc, _view.Id)
                .WherePasses(combinedFilter)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var elem in revitElements)
            {
                try
                {
                    var taggable = ConvertToTaggableElement(elem);
                    if (taggable != null)
                    {
                        elements.Add(taggable);
                    }
                }
                catch
                {
                    // Skip invalid elements
                }
            }

            return elements;
        }

        /// <summary>
        /// Build a training sample from an element and its tag.
        /// </summary>
        /// <param name="allTagLocations">All tags' head positions in view coords.</param>
        /// <param name="skipIndex">Index in allTagLocations to exclude (the current tag itself). Use -1 to include all.</param>
        private TrainingSample BuildTrainingSample(
            Element element,
            IndependentTag tag,
            List<TaggableElement> allElements,
            List<Point2D> allTagLocations = null,
            int skipIndex = -1)
        {
            var taggable = ConvertToTaggableElement(element);
            if (taggable == null) return null;

            var tagHead = tag.TagHeadPosition;
            var tagLocation = ConvertToViewCoordinates(tagHead);

            var elementCenter = taggable.Center;

            var offsetX = tagLocation.X - elementCenter.X;
            var offsetY = tagLocation.Y - elementCenter.Y;

            var position = DetermineTagPosition(offsetX, offsetY);

            var context = _contextAnalyzer.Analyze(taggable, allElements);

            var tagText = GetTagText(tag);

            var (tagWidth, tagHeight) = EstimateTagSize(tag, tagText);

            var (alignedRow, alignedCol) = CheckAlignmentWithOtherTags(tagLocation, allTagLocations ?? new List<Point2D>(), skipIndex);

            // Calculate leader length
            var leaderLength = tag.HasLeader ? CalculateLeaderLength(tag, element) : 0;

            return new TrainingSample
            {
                Id = $"sample_{element.Id.Value}",
                Element = new TrainingElement
                {
                    Category = element.Category?.BuiltInCategory.ToString() ?? "Unknown",
                    FamilyName = GetFamilyName(element),
                    TypeName = GetTypeName(element),
                    Orientation = taggable.AngleRadians * 180.0 / Math.PI,
                    IsLinear = taggable.IsLinearElement,
                    Length = taggable.LengthFeet,
                    Width = taggable.ViewBounds.Width,
                    Height = taggable.ViewBounds.Height,
                    Diameter = GetDiameter(element),
                    SystemType = GetSystemType(element),
                    CenterX = elementCenter.X,
                    CenterY = elementCenter.Y
                },
                Context = new TrainingContext
                {
                    Density = context.Density.ToString().ToLower(),
                    NeighborCount = context.NeighborCount,
                    HasNeighborAbove = context.HasNeighborAbove,
                    HasNeighborBelow = context.HasNeighborBelow,
                    HasNeighborLeft = context.HasNeighborLeft,
                    HasNeighborRight = context.HasNeighborRight,
                    DistanceToNearestAbove = context.DistanceToNearestAbove,
                    DistanceToNearestBelow = context.DistanceToNearestBelow,
                    DistanceToNearestLeft = context.DistanceToNearestLeft,
                    DistanceToNearestRight = context.DistanceToNearestRight,
                    DistanceToWall = context.DistanceToWall,
                    ParallelElementsCount = context.ParallelElementsCount,
                    IsInGroup = context.IsInGroup
                },
                Tag = new TrainingTag
                {
                    Position = position.ToString(),
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    HasLeader = tag.HasLeader,
                    LeaderLength = leaderLength,
                    Rotation = DetermineRotation(tag),
                    AlignedWithRow = alignedRow,
                    AlignedWithColumn = alignedCol,
                    TagText = tagText,
                    TagWidth = tagWidth,
                    TagHeight = tagHeight
                },
                ViewScale = (int)_view.Scale
            };
        }

        #region Helper Methods

        private bool IsSupportedCategory(Category category)
        {
            if (category == null) return false;

            var supportedCategories = new[]
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
                BuiltInCategory.OST_DuctTerminal
            };

            return supportedCategories.Contains(category.BuiltInCategory);
        }

        private TaggableElement ConvertToTaggableElement(Element element)
        {
            if (element == null) return null;

            var bbox = element.get_BoundingBox(_view);
            if (bbox == null) return null;

            var center = (bbox.Min + bbox.Max) / 2;
            var viewCenter = ConvertToViewCoordinates(center);

            var viewMin = ConvertToViewCoordinates(bbox.Min);
            var viewMax = ConvertToViewCoordinates(bbox.Max);

            var isLinear = element is MEPCurve;
            var length = 0.0;
            var angle = 0.0;
            Point2D? startPt = null;
            Point2D? endPt = null;

            if (element is MEPCurve curve)
            {
                var line = (curve.Location as LocationCurve)?.Curve as Line;
                if (line != null)
                {
                    length = line.Length;
                    var direction = line.Direction;
                    angle = Math.Atan2(direction.Y, direction.X);
                    startPt = ConvertToViewCoordinates(line.GetEndPoint(0));
                    endPt = ConvertToViewCoordinates(line.GetEndPoint(1));
                }
            }

            return new TaggableElement
            {
                ElementId = element.Id.Value,
                CategoryName = element.Category?.Name ?? "Unknown",
                BuiltInCategoryName = element.Category?.BuiltInCategory.ToString() ?? "Unknown",
                FamilyName = GetFamilyName(element),
                TypeName = GetTypeName(element),
                ViewBounds = new BoundingBox2D(
                    Math.Min(viewMin.X, viewMax.X),
                    Math.Min(viewMin.Y, viewMax.Y),
                    Math.Max(viewMin.X, viewMax.X),
                    Math.Max(viewMin.Y, viewMax.Y)),
                Center = viewCenter,
                IsLinearElement = isLinear,
                LengthFeet = length,
                AngleRadians = angle,
                StartPoint = startPt,
                EndPoint = endPt,
                SystemName = GetSystemType(element)
            };
        }

        private Point2D ConvertToViewCoordinates(XYZ point)
        {
            var origin = _view.Origin;
            var rightDir = _view.RightDirection;
            var upDir = _view.UpDirection;

            var relative = point - origin;
            return new Point2D(
                relative.DotProduct(rightDir),
                relative.DotProduct(upDir));
        }

        private string GetFamilyName(Element element)
        {
            try
            {
                if (element is FamilyInstance fi)
                    return fi.Symbol?.Family?.Name ?? "Unknown";
                    
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var type = _doc.GetElement(typeId);
                    if (type is FamilySymbol fs)
                        return fs.Family?.Name ?? "Unknown";
                }
            }
            catch { }
            return "Unknown";
        }

        private string GetTypeName(Element element)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    var type = _doc.GetElement(typeId);
                    return type?.Name ?? "Unknown";
                }
            }
            catch { }
            return "Unknown";
        }

        private double GetDiameter(Element element)
        {
            try
            {
                var param = element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?? element.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                if (param != null)
                    return param.AsDouble();
            }
            catch { }
            return 0;
        }

        private string GetSystemType(Element element)
        {
            try
            {
                if (element is MEPCurve curve)
                {
                    var system = curve.MEPSystem;
                    if (system != null)
                        return system.Name;
                }
                
                var param = element.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
                if (param != null)
                    return param.AsString() ?? "Unknown";
            }
            catch { }
            return "Unknown";
        }

        private string GetTagText(IndependentTag tag)
        {
            try
            {
                return tag.TagText ?? "";
            }
            catch
            {
                return "";
            }
        }

        private (double width, double height) EstimateTagSize(IndependentTag tag, string text)
        {
            try
            {
                var scale = _view.Scale > 0 ? _view.Scale : 100;
                var inverseScale = 100.0 / scale;

                var charCount = text?.Length ?? 5;
                var lineCount = text?.Split('\n').Length ?? 1;

                var width = Math.Max(charCount * 0.1 * inverseScale, 1.0);
                var height = Math.Max(lineCount * 0.4 * inverseScale, 0.5);

                return (width, height);
            }
            catch
            {
                return (2.0, 0.8);
            }
        }

        private TagPosition DetermineTagPosition(double offsetX, double offsetY)
        {
            var threshold = 0.5; // feet

            bool isTop = offsetY > threshold;
            bool isBottom = offsetY < -threshold;
            bool isRight = offsetX > threshold;
            bool isLeft = offsetX < -threshold;

            if (isTop && isRight) return TagPosition.TopRight;
            if (isTop && isLeft) return TagPosition.TopLeft;
            if (isTop) return TagPosition.TopCenter;
            if (isBottom && isRight) return TagPosition.BottomRight;
            if (isBottom && isLeft) return TagPosition.BottomLeft;
            if (isBottom) return TagPosition.BottomCenter;
            if (isRight) return TagPosition.Right;
            if (isLeft) return TagPosition.Left;
            return TagPosition.Center;
        }

        private string DetermineRotation(IndependentTag tag)
        {
            try
            {
                var orientation = tag.TagOrientation;
                return orientation == TagOrientation.Vertical ? "Vertical" : "Horizontal";
            }
            catch
            {
                return "Horizontal";
            }
        }

        private double CalculateLeaderLength(IndependentTag tag, Element element)
        {
            try
            {
                var tagHead = tag.TagHeadPosition;
                var bbox = element.get_BoundingBox(_view);
                if (bbox == null) return 0;

                var center = (bbox.Min + bbox.Max) / 2;
                return tagHead.DistanceTo(center);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Check if this tag aligns with other tags (same Y = row, same X = column).
        /// </summary>
        private (bool alignedRow, bool alignedCol) CheckAlignmentWithOtherTags(
            Point2D tagLocation, List<Point2D> allTagLocations, int skipIndex = -1)
        {
            const double tolerance = 1.0; // feet
            bool alignedRow = false, alignedCol = false;

            for (int i = 0; i < allTagLocations.Count; i++)
            {
                if (i == skipIndex) continue;
                var p = allTagLocations[i];
                if (!alignedRow && Math.Abs(p.Y - tagLocation.Y) <= tolerance && Math.Abs(p.X - tagLocation.X) > tolerance)
                    alignedRow = true;
                if (!alignedCol && Math.Abs(p.X - tagLocation.X) <= tolerance && Math.Abs(p.Y - tagLocation.Y) > tolerance)
                    alignedCol = true;
                if (alignedRow && alignedCol) break;
            }

            return (alignedRow, alignedCol);
        }

        private string GuessDiscipline(List<TrainingSample> samples)
        {
            if (samples.Count == 0) return "Unknown";

            var categories = samples
                .Select(s => s.Element?.Category ?? "Unknown")
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? "Unknown";

            if (categories.Contains("Pipe")) return "Sanitary";
            if (categories.Contains("Duct")) return "HVAC";
            if (categories.Contains("CableTray") || categories.Contains("Conduit")) return "Electrical";
            if (categories.Contains("Equipment")) return "Equipment";
            return "MEP";
        }

        #endregion
    }

    /// <summary>
    /// Result of training data export.
    /// </summary>
    public class TrainingExportResult
    {
        public bool Success { get; set; }
        public string ViewName { get; set; }
        public int ViewScale { get; set; }
        public int TotalElements { get; set; }
        public int ExportedSamples { get; set; }
        public string OutputPath { get; set; }
        public DateTime Timestamp { get; set; }
        public List<string> Errors { get; set; } = new();
        public TrainingDataFile TrainingData { get; set; }
    }
}
