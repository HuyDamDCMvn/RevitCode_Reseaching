using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartTag.ML;
using SmartTag.Models;
using SmartTag.Services;

namespace SmartTag
{
    /// <summary>
    /// Request types for SmartTag operations.
    /// </summary>
    public enum SmartTagRequestType
    {
        None,
        GetCategoryStats,
        ExecuteAutoTag,
        PreviewPlacements,
        ClearPreview,
        SelectElements,
        // Preview workflow
        CreatePreviewTags,      // Create tags for preview (can undo)
        ConfirmPreviewTags,     // Confirm and keep preview tags
        UndoPreviewTags,        // Undo/delete preview tags
        // Export training data from current view (to update rules/patterns)
        ExportTrainingData,
        // Dimension operations
        ExecuteAutoDimension,
        GetDimensionTypes,
        DimensionSelection
    }

    /// <summary>
    /// Request DTO for SmartTag operations.
    /// </summary>
    public sealed class SmartTagRequest
    {
        public SmartTagRequestType Type { get; }
        public TagSettings Settings { get; }
        public List<long> ElementIds { get; }
        public DimensionSettings DimensionSettings { get; }

        private SmartTagRequest(
            SmartTagRequestType type, 
            TagSettings settings = null, 
            List<long> elementIds = null,
            DimensionSettings dimensionSettings = null)
        {
            Type = type;
            Settings = settings;
            ElementIds = elementIds;
            DimensionSettings = dimensionSettings;
        }

        public static SmartTagRequest GetCategoryStats() => new(SmartTagRequestType.GetCategoryStats);
        public static SmartTagRequest ExecuteAutoTag(TagSettings settings) => new(SmartTagRequestType.ExecuteAutoTag, settings);
        public static SmartTagRequest PreviewPlacements(TagSettings settings) => new(SmartTagRequestType.PreviewPlacements, settings);
        public static SmartTagRequest ClearPreview() => new(SmartTagRequestType.ClearPreview);
        public static SmartTagRequest SelectElements(List<long> elementIds) => new(SmartTagRequestType.SelectElements, null, elementIds);
        
        // Preview workflow requests
        public static SmartTagRequest CreatePreviewTags(TagSettings settings) => new(SmartTagRequestType.CreatePreviewTags, settings);
        public static SmartTagRequest ConfirmPreviewTags() => new(SmartTagRequestType.ConfirmPreviewTags);
        public static SmartTagRequest UndoPreviewTags() => new(SmartTagRequestType.UndoPreviewTags);
        public static SmartTagRequest ExportTrainingData() => new(SmartTagRequestType.ExportTrainingData);

        // Dimension requests
        public static SmartTagRequest ExecuteAutoDimension(DimensionSettings settings) => 
            new(SmartTagRequestType.ExecuteAutoDimension, null, null, settings);
        public static SmartTagRequest GetDimensionTypes() => new(SmartTagRequestType.GetDimensionTypes);
        public static SmartTagRequest DimensionSelection(DimensionSettings settings, List<long> elementIds) => 
            new(SmartTagRequestType.DimensionSelection, null, elementIds, settings);
    }

    /// <summary>
    /// External event handler for SmartTag - ONLY place that calls Revit API.
    /// </summary>
    public class SmartTagHandler : IExternalEventHandler
    {
        private SmartTagRequest _request;
        private readonly object _lock = new();
        private ExternalEvent _externalEvent;
        
        // Preview state - stores created preview tag IDs for undo
        private List<ElementId> _previewTagIds = new List<ElementId>();
        private bool _hasPreviewTags = false;

        /// <summary>
        /// Callback when category stats are loaded.
        /// </summary>
        public event Action<List<CategoryTagConfig>> OnCategoryStatsLoaded;

        /// <summary>
        /// Callback when auto-tag operation completes.
        /// </summary>
        public event Action<TagResult> OnAutoTagCompleted;

        /// <summary>
        /// Callback when placements are calculated (for preview).
        /// </summary>
        public event Action<List<TagPlacement>, List<TaggableElement>> OnPlacementsCalculated;
        
        /// <summary>
        /// Callback when preview tags are created (user can confirm or undo).
        /// </summary>
        public event Action<TagResult, bool> OnPreviewTagsCreated; // bool = hasPreviewTags

        /// <summary>
        /// Callback on error.
        /// </summary>
        public event Action<string> OnError;

        /// <summary>
        /// Callback for status updates.
        /// </summary>
        public event Action<string> OnStatusUpdate;

        /// <summary>
        /// Callback when auto-dimension completes.
        /// </summary>
        public event Action<DimensionResult> OnAutoDimensionCompleted;

        /// <summary>
        /// Callback when dimension types are loaded.
        /// </summary>
        public event Action<List<(long Id, string Name)>> OnDimensionTypesLoaded;

        /// <summary>
        /// Callback when training data export completes. (outputPath, sampleCount, success)
        /// </summary>
        public event Action<string, int, bool> OnExportTrainingDataCompleted;

        public void SetExternalEvent(ExternalEvent externalEvent)
        {
            _externalEvent = externalEvent;
        }

        public void SetRequest(SmartTagRequest request)
        {
            lock (_lock) { _request = request; }
        }

        public void Execute(UIApplication app)
        {
            SmartTagRequest request;
            lock (_lock) { request = _request; _request = null; }
            if (request == null) return;

            try
            {
                switch (request.Type)
                {
                    case SmartTagRequestType.GetCategoryStats:
                        ExecuteGetCategoryStats(app);
                        break;
                    case SmartTagRequestType.ExecuteAutoTag:
                        ExecuteAutoTag(app, request.Settings);
                        break;
                    case SmartTagRequestType.PreviewPlacements:
                        ExecutePreviewPlacements(app, request.Settings);
                        break;
                    case SmartTagRequestType.SelectElements:
                        ExecuteSelectElements(app, request.ElementIds);
                        break;
                    case SmartTagRequestType.CreatePreviewTags:
                        ExecuteCreatePreviewTags(app, request.Settings);
                        break;
                    case SmartTagRequestType.ConfirmPreviewTags:
                        ExecuteConfirmPreviewTags(app);
                        break;
                    case SmartTagRequestType.UndoPreviewTags:
                        ExecuteUndoPreviewTags(app);
                        break;
                    case SmartTagRequestType.ExecuteAutoDimension:
                        ExecuteAutoDimension(app, request.DimensionSettings);
                        break;
                    case SmartTagRequestType.GetDimensionTypes:
                        ExecuteGetDimensionTypes(app);
                        break;
                    case SmartTagRequestType.DimensionSelection:
                        ExecuteDimensionSelection(app, request.DimensionSettings, request.ElementIds);
                        break;
                    case SmartTagRequestType.ExportTrainingData:
                        ExecuteExportTrainingData(app);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Error: {ex.Message}");
            }
        }

        private void ExecuteGetCategoryStats(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a floor plan, ceiling plan, section, or elevation view");
                return;
            }

            OnStatusUpdate?.Invoke("Analyzing categories...");

            var collector = new ElementCollector(doc, view);
            var stats = collector.GetCategoryStats();
            
            // OPTIMIZATION: Don't load tag types here - it's slow
            // Tag types will be loaded lazily when user selects a category
            // or in background after initial load
            
            // Quick dispatch to UI first (fast feedback)
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnCategoryStatsLoaded?.Invoke(stats);
            }));

            OnStatusUpdate?.Invoke($"Found {stats.Count} taggable categories");
            
            // THEN load tag types in background (non-blocking)
            // This runs after UI is already updated
            foreach (var stat in stats)
            {
                stat.AvailableTagTypes = collector.GetTagTypesForCategory(stat.Category);
                
                if (stat.AvailableTagTypes.Count > 0)
                {
                    stat.SelectedTagType = stat.AvailableTagTypes[0];
                }
            }
            
            // Notify UI again with tag types loaded
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnCategoryStatsLoaded?.Invoke(stats);
            }));
        }

        private void ExecuteAutoTag(UIApplication app, TagSettings settings)
        {
            var stopwatch = Stopwatch.StartNew();

            if (settings == null)
            {
                OnError?.Invoke("Settings are null");
                return;
            }

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a floor plan, ceiling plan, section, or elevation view");
                return;
            }

            if (settings.Categories == null || settings.Categories.Count == 0)
            {
                OnError?.Invoke("No categories selected");
                return;
            }

            OnStatusUpdate?.Invoke("Collecting elements...");

            // 1. Collect elements
            var elementCollector = new ElementCollector(doc, view);
            var elements = elementCollector.GetTaggableElements(settings.Categories);
            var existingTags = elementCollector.GetExistingTags();
            var annotations = elementCollector.GetAnnotationBounds();

            if (elements == null)
                elements = new List<TaggableElement>();
            if (existingTags == null)
                existingTags = new List<(long, long, BoundingBox2D)>();

            OnStatusUpdate?.Invoke($"Found {elements.Count} elements, calculating placements...");

            var placementService = new TagPlacementService(doc, view);
            placementService.Initialize(elements, existingTags, annotations);
            var placements = settings.UseQuickMode
                ? placementService.CalculatePlacements(elements, settings)
                : CalculatePlacementsWithML(doc, view, elements, settings);

            if (placements == null)
                placements = new List<TagPlacement>();

            // 3. Resolve collisions
            OnStatusUpdate?.Invoke("Resolving collisions...");
            placementService.ResolveCollisions(placements);

            // 4. Refinement loop: re-scan overlap, re-place or align until clean (max 3 iterations)
            OnStatusUpdate?.Invoke("Refining placements (re-place/align)...");
            placementService.RefinePlacementsIterative(placements, elements, settings, maxRefineIterations: 3);

            OnStatusUpdate?.Invoke($"Creating {placements.Count} tags...");

            // 4. Create tags in transaction with error handling
            var creationService = new TagCreationService(doc, view);
            TagResult result = new TagResult();

            using (var trans = new Transaction(doc, "Smart Tag - Auto Tag"))
            {
                try
                {
                    var transResult = trans.Start();
                    if (transResult != TransactionStatus.Started)
                    {
                        OnError?.Invoke("Failed to start transaction");
                        return;
                    }
                    
                    result = creationService.CreateTags(placements, settings);
                    
                    if (result.TagsCreated > 0)
                    {
                        var commitResult = trans.Commit();
                        if (commitResult != TransactionStatus.Committed)
                        {
                            result.Warnings.Add("Transaction commit returned: " + commitResult.ToString());
                        }
                    }
                    else
                    {
                        // No tags created, rollback
                        trans.RollBack();
                    }
                }
                catch (Exception ex)
                {
                    // Rollback on any error
                    if (trans.HasStarted())
                    {
                        try { trans.RollBack(); } catch { /* Ignore rollback failure */ }
                    }
                    result.Warnings.Add($"Transaction error: {ex.Message}");
                    OnError?.Invoke($"Transaction failed: {ex.Message}");
                }
            }

            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            result.CollisionsResolved = placements.Count(p => p.Score > 100); // Had collision penalty
            
            // Count elements that were skipped due to no valid placement
            var elementsWithPlacements = new HashSet<long>(placements.Select(p => p.ElementId));
            var skippedElements = elements
                .Where(e => !e.HasExistingTag && !elementsWithPlacements.Contains(e.ElementId))
                .ToList();
            result.ElementsSkippedNoSpace = skippedElements.Count;
            result.ElementsAlreadyTagged = elements.Count(e => e.HasExistingTag);
            result.SkippedElementIds = skippedElements.Select(e => e.ElementId).ToList();

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnAutoTagCompleted?.Invoke(result);
            }));

            OnStatusUpdate?.Invoke($"Created {result.TagsCreated} tags in {result.Duration.TotalSeconds:F1}s");
        }

        private void ExecutePreviewPlacements(UIApplication app, TagSettings settings)
        {
            if (settings == null) return;

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a taggable view");
                return;
            }

            if (settings.Categories == null || settings.Categories.Count == 0)
            {
                return; // No categories selected, clear preview
            }

            OnStatusUpdate?.Invoke("Calculating preview...");

            // Collect elements
            var elementCollector = new ElementCollector(doc, view);
            var elements = elementCollector.GetTaggableElements(settings.Categories);
            var existingTags = elementCollector.GetExistingTags();
            var annotations = elementCollector.GetAnnotationBounds();

            // Calculate placements (without creating)
            var placementService = new TagPlacementService(doc, view);
            placementService.Initialize(elements, existingTags, annotations);
            var placements = settings.UseQuickMode
                ? placementService.CalculatePlacements(elements, settings)
                : CalculatePlacementsWithML(doc, view, elements, settings);
            placementService.ResolveCollisions(placements);
            placementService.RefinePlacementsIterative(placements, elements, settings, maxRefineIterations: 2);

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnPlacementsCalculated?.Invoke(placements, elements);
            }));

            OnStatusUpdate?.Invoke($"Preview: {placements.Count} tags to create");
        }

        private void ExecuteSelectElements(UIApplication app, List<long> elementIds)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null || elementIds == null || elementIds.Count == 0) return;

            try
            {
                var ids = elementIds.Select(id => new ElementId(id)).ToList();
                uidoc.Selection.SetElementIds(ids);
                OnStatusUpdate?.Invoke($"Selected {ids.Count} element(s)");
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Selection failed: {ex.Message}");
            }
        }

        private bool IsTaggableView(View view)
        {
            return view is ViewPlan || 
                   view is ViewSection || 
                   view is ViewDrafting ||
                   view is ViewSheet ||
                   (view.ViewType == ViewType.CeilingPlan) ||
                   (view.ViewType == ViewType.FloorPlan) ||
                   (view.ViewType == ViewType.EngineeringPlan) ||
                   (view.ViewType == ViewType.AreaPlan) ||
                   (view.ViewType == ViewType.Elevation) ||
                   (view.ViewType == ViewType.Section) ||
                   (view.ViewType == ViewType.Detail);
        }

        private List<TagPlacement> CalculatePlacementsWithML(
            Document doc,
            View view,
            List<TaggableElement> elements,
            TagSettings settings)
        {
            try
            {
                var options = new PlacementEngineOptions
                {
                    TrainingDataPath = GetTrainingExportDirectory(),
                    UseKNN = true,
                    UseCSP = true,
                    UseRules = true,
                    UsePatterns = true
                };

                var engine = new PlacementEngine(doc, view, options);
                return engine.CalculatePlacements(elements, settings);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"ML placement failed: {ex.Message}");
                return new List<TagPlacement>();
            }
        }

        #region Preview Workflow

        /// <summary>
        /// Create preview tags - user can see result before confirming.
        /// </summary>
        private void ExecuteCreatePreviewTags(UIApplication app, TagSettings settings)
        {
            var stopwatch = Stopwatch.StartNew();

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a floor plan, ceiling plan, section, or elevation view");
                return;
            }

            if (settings == null)
            {
                OnError?.Invoke("Settings are null");
                return;
            }

            if (settings.Categories == null || settings.Categories.Count == 0)
            {
                OnError?.Invoke("No categories selected");
                return;
            }

            // Clear any existing preview first (suppress callback to avoid resetting IsPreviewMode)
            if (_hasPreviewTags && _previewTagIds.Count > 0)
            {
                ExecuteUndoPreviewTags(app, suppressCallback: true);
            }

            OnStatusUpdate?.Invoke("Collecting elements for preview...");

            // 1. Collect elements
            var elementCollector = new ElementCollector(doc, view);
            var elements = elementCollector.GetTaggableElements(settings.Categories);
            var existingTags = elementCollector.GetExistingTags();
            var annotations = elementCollector.GetAnnotationBounds();

            if (elements == null) elements = new List<TaggableElement>();
            if (existingTags == null) existingTags = new List<(long, long, BoundingBox2D)>();

            OnStatusUpdate?.Invoke($"Found {elements.Count} elements, calculating placements...");

            // 2. Calculate placements
            var placementService = new TagPlacementService(doc, view);
            placementService.Initialize(elements, existingTags, annotations);
            var placements = settings.UseQuickMode
                ? placementService.CalculatePlacements(elements, settings)
                : CalculatePlacementsWithML(doc, view, elements, settings);
            if (placements == null) placements = new List<TagPlacement>();

            // 3. Resolve collisions
            OnStatusUpdate?.Invoke("Resolving collisions...");
            placementService.ResolveCollisions(placements);

            // 4. Refinement loop: re-scan overlap, re-place or align (max 3 iterations)
            OnStatusUpdate?.Invoke("Refining placements (re-place/align)...");
            placementService.RefinePlacementsIterative(placements, elements, settings, maxRefineIterations: 3);

            OnStatusUpdate?.Invoke($"Creating {placements.Count} preview tags...");

            // 5. Create tags in transaction
            var creationService = new TagCreationService(doc, view);
            TagResult result = new TagResult();
            _previewTagIds.Clear();

            using (var trans = new Transaction(doc, "Smart Tag - Preview Tags"))
            {
                try
                {
                    var transResult = trans.Start();
                    if (transResult != TransactionStatus.Started)
                    {
                        OnError?.Invoke("Failed to start transaction");
                        return;
                    }
                    
                    result = creationService.CreateTags(placements, settings);
                    
                    // Store created tag IDs for potential undo
                    _previewTagIds = (result?.CreatedTagIds != null)
                        ? result.CreatedTagIds.Select(id => new ElementId(id)).ToList()
                        : new List<ElementId>();
                    
                    if (result.TagsCreated > 0)
                    {
                        var commitResult = trans.Commit();
                        if (commitResult == TransactionStatus.Committed)
                        {
                            _hasPreviewTags = true;
                        }
                        else
                        {
                            result.Warnings.Add("Transaction commit returned: " + commitResult.ToString());
                            _hasPreviewTags = false;
                        }
                    }
                    else
                    {
                        trans.RollBack();
                        _hasPreviewTags = false;
                    }
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted())
                    {
                        try { trans.RollBack(); } catch { }
                    }
                    result.Warnings.Add($"Transaction error: {ex.Message}");
                    OnError?.Invoke($"Preview failed: {ex.Message}");
                    _hasPreviewTags = false;
                    return;
                }
            }

            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            result.CollisionsResolved = placements.Count(p => p.Score > 100);
            
            var elementsWithPlacements = new HashSet<long>(placements.Select(p => p.ElementId));
            var skippedElements = elements
                .Where(e => !e.HasExistingTag && !elementsWithPlacements.Contains(e.ElementId))
                .ToList();
            result.ElementsSkippedNoSpace = skippedElements.Count;
            result.ElementsAlreadyTagged = elements.Count(e => e.HasExistingTag);

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnPreviewTagsCreated?.Invoke(result, _hasPreviewTags);
            }));

            OnStatusUpdate?.Invoke($"Preview: {result.TagsCreated} tags created. Confirm or Undo?");
        }

        /// <summary>
        /// Confirm preview tags - keep them permanently.
        /// </summary>
        private void ExecuteConfirmPreviewTags(UIApplication app)
        {
            if (!_hasPreviewTags || _previewTagIds.Count == 0)
            {
                OnError?.Invoke("No preview tags to confirm");
                return;
            }

            var count = _previewTagIds.Count;
            _previewTagIds.Clear();
            _hasPreviewTags = false;

            OnStatusUpdate?.Invoke($"Confirmed {count} tags");

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnPreviewTagsCreated?.Invoke(
                    new TagResult { TagsCreated = count },
                    false);
            }));

            var uidoc = app.ActiveUIDocument;
            if (uidoc != null)
            {
                ExecuteGetCategoryStats(app);
            }
        }

        /// <summary>
        /// Undo preview tags - delete all preview tags.
        /// </summary>
        private void ExecuteUndoPreviewTags(UIApplication app, bool suppressCallback = false)
        {
            if (!_hasPreviewTags || _previewTagIds.Count == 0)
            {
                OnStatusUpdate?.Invoke("No preview tags to undo");
                _hasPreviewTags = false;
                _previewTagIds.Clear();
                return;
            }

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var deleteCount = _previewTagIds.Count;

            using (var trans = new Transaction(doc, "Smart Tag - Undo Preview"))
            {
                try
                {
                    trans.Start();
                    
                    // Filter out invalid IDs (elements that may have been deleted by user)
                    var validIds = _previewTagIds
                        .Where(id => doc.GetElement(id) != null)
                        .ToList();
                    
                    if (validIds.Count > 0)
                    {
                        doc.Delete(validIds);
                    }
                    
                    trans.Commit();
                    
                    OnStatusUpdate?.Invoke($"Undone: deleted {validIds.Count} preview tags");
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted())
                    {
                        try { trans.RollBack(); } catch { }
                    }
                    OnError?.Invoke($"Undo failed: {ex.Message}");
                }
            }

            _previewTagIds.Clear();
            _hasPreviewTags = false;

            if (!suppressCallback)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnPreviewTagsCreated?.Invoke(
                        new TagResult { TagsCreated = 0 },
                        false);
                }));
            }
        }

        /// <summary>
        /// Check if there are preview tags that can be undone.
        /// </summary>
        public bool HasPreviewTags => _hasPreviewTags && _previewTagIds.Count > 0;

        /// <summary>
        /// Export training data from current view (tagged elements) to JSON for updating rules/patterns.
        /// </summary>
        private void ExecuteExportTrainingData(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a floor plan, ceiling plan, section, or elevation view");
                return;
            }

            OnStatusUpdate?.Invoke("Exporting training data from current view...");

            try
            {
                var exporter = new TrainingDataExporter(doc, view);
                var safeName = string.Join("_", (view.Name ?? "View").Split(System.IO.Path.GetInvalidFileNameChars()));
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                var fileName = $"exported_{safeName}_{timestamp}.json";
                var exportDir = GetTrainingExportDirectory();
                var outputPath = System.IO.Path.Combine(exportDir, fileName);

                var result = exporter.Export(outputPath);

                if (result.Success && result.ExportedSamples > 0)
                {
                    // Self-learning: ingest export and update learned_overrides.json
                    var ingestion = new ExportIngestionService();
                    var ingestResult = ingestion.IngestAndUpdate(result.OutputPath);
                    if (ingestResult.Success)
                    {
                        // Refresh template + radius + weights from newly exported ground truth
                        TemplateLibrary.Instance.ForceReload();
                        TagPlacementRadiusService.Instance.Reset();
                        AutoTuneWeightsService.Instance.Reset();

                        OnStatusUpdate?.Invoke($"Exported {result.ExportedSamples} samples. Learned: {ingestResult.Message}");
                    }
                    else
                    {
                        OnStatusUpdate?.Invoke($"Exported {result.ExportedSamples} samples to {result.OutputPath}");
                    }
                }
                else if (result.Success)
                {
                    OnStatusUpdate?.Invoke("Export completed but no tagged elements in view.");
                }
                else
                {
                    var msg = result.Errors?.Count > 0 ? string.Join("; ", result.Errors) : "Export failed";
                    OnError?.Invoke(msg);
                }

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnExportTrainingDataCompleted?.Invoke(
                        result.OutputPath ?? "",
                        result.ExportedSamples,
                        result.Success);
                }));
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Export failed: {ex.Message}");
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnExportTrainingDataCompleted?.Invoke("", 0, false);
                }));
            }
        }

        private static string GetTrainingExportDirectory()
        {
            var folder = DataPathResolver.ResolveFolder("Training/annotated");
            if (folder != null) return folder;

            var fallback = DataPathResolver.Resolve("Training/annotated/.placeholder", "SmartTag_annotated");
            var dir = System.IO.Path.GetDirectoryName(fallback);
            if (dir != null && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            return dir ?? System.IO.Path.GetTempPath();
        }

        #endregion

        #region Dimension Operations

        private void ExecuteAutoDimension(UIApplication app, DimensionSettings settings)
        {
            if (settings == null)
            {
                OnError?.Invoke("Dimension settings are null");
                return;
            }

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a floor plan, section, or elevation view");
                return;
            }

            OnStatusUpdate?.Invoke("Creating dimensions for openings...");

            var dimensionService = new DimensionService(doc, view);
            var result = dimensionService.CreateAutoDimensions(settings);

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnAutoDimensionCompleted?.Invoke(result);
            }));

            if (result.Warnings.Count > 0)
            {
                OnStatusUpdate?.Invoke($"Created {result.DimensionsCreated} dimensions. Warnings: {result.Warnings.Count}");
            }
            else
            {
                OnStatusUpdate?.Invoke($"Created {result.DimensionsCreated} dimensions in {result.Duration.TotalSeconds:F1}s");
            }
        }

        private void ExecuteGetDimensionTypes(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            var dimensionService = new DimensionService(doc, view);
            var types = dimensionService.GetDimensionTypes();

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnDimensionTypesLoaded?.Invoke(types);
            }));
        }

        private void ExecuteDimensionSelection(UIApplication app, DimensionSettings settings, List<long> elementIds)
        {
            if (settings == null)
            {
                OnError?.Invoke("Dimension settings are null");
                return;
            }

            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                OnError?.Invoke("No active document");
                return;
            }

            var doc = uidoc.Document;
            var view = uidoc.ActiveView;

            if (view == null || !IsTaggableView(view))
            {
                OnError?.Invoke("Please switch to a floor plan, section, or elevation view");
                return;
            }

            var selectedIds = elementIds?.Select(id => new ElementId(id)).ToList() ?? new List<ElementId>();
            
            if (selectedIds.Count == 0)
            {
                // Try to get current selection
                selectedIds = uidoc.Selection.GetElementIds().ToList();
            }

            if (selectedIds.Count == 0)
            {
                OnError?.Invoke("No elements selected. Please select openings to dimension.");
                return;
            }

            OnStatusUpdate?.Invoke($"Dimensioning {selectedIds.Count} selected elements...");

            var dimensionService = new DimensionService(doc, view);
            var result = dimensionService.CreateDimensionsForSelection(selectedIds, settings);

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnAutoDimensionCompleted?.Invoke(result);
            }));

            OnStatusUpdate?.Invoke($"Created {result.DimensionsCreated} dimensions");
        }

        #endregion

        public string GetName() => "SmartTag.Handler";
    }
}
