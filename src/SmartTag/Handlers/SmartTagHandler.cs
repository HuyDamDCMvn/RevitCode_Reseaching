using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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
                    case SmartTagRequestType.ExecuteAutoDimension:
                        ExecuteAutoDimension(app, request.DimensionSettings);
                        break;
                    case SmartTagRequestType.GetDimensionTypes:
                        ExecuteGetDimensionTypes(app);
                        break;
                    case SmartTagRequestType.DimensionSelection:
                        ExecuteDimensionSelection(app, request.DimensionSettings, request.ElementIds);
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

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                OnCategoryStatsLoaded?.Invoke(stats);
            }));

            OnStatusUpdate?.Invoke($"Found {stats.Count} taggable categories");
        }

        private void ExecuteAutoTag(UIApplication app, TagSettings settings)
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
            var annotations = elementCollector.GetAnnotationBounds(); // NEW: collect annotations

            OnStatusUpdate?.Invoke($"Found {elements.Count} elements, calculating placements...");

            // 2. Calculate placements (with annotation collision avoidance)
            var placementService = new TagPlacementService(doc, view);
            placementService.Initialize(elements, existingTags, annotations); // Pass annotations
            var placements = placementService.CalculatePlacements(elements, settings);

            // 3. Resolve collisions
            OnStatusUpdate?.Invoke("Resolving collisions...");
            placementService.ResolveCollisions(placements);

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
            var placements = placementService.CalculatePlacements(elements, settings);
            placementService.ResolveCollisions(placements);

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

        #region Dimension Operations

        private void ExecuteAutoDimension(UIApplication app, DimensionSettings settings)
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
