using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartTag.Models;

namespace SmartTag
{
    /// <summary>
    /// ViewModel for SmartTag window.
    /// Never calls Revit API directly - uses ExternalEvent pattern.
    /// </summary>
    public partial class SmartTagViewModel : ObservableObject
    {
        private readonly ExternalEvent _externalEvent;
        private readonly SmartTagHandler _handler;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        private ObservableCollection<CategoryTagConfigViewModel> _categories = new();

        [ObservableProperty]
        private bool _skipTaggedElements = true;

        [ObservableProperty]
        private bool _addLeaders = true;

        [ObservableProperty]
        private bool _alignTags = true;

        [ObservableProperty]
        private TagPosition _selectedPosition = TagPosition.TopRight;

        [ObservableProperty]
        private int _previewTagCount;

        [ObservableProperty]
        private int _selectedCategoryCount;

        [ObservableProperty]
        private int _totalElementCount;

        // Last result for display
        [ObservableProperty]
        private TagResult _lastResult;
        
        // Preview state
        [ObservableProperty]
        private bool _hasPreviewTags;
        
        [ObservableProperty]
        private bool _isPreviewMode;
        
        [ObservableProperty]
        private int _previewCreatedCount;

        // Dimension properties
        [ObservableProperty]
        private DimensionResult _lastDimensionResult;

        [ObservableProperty]
        private ObservableCollection<DimensionTypeItem> _dimensionTypes = new();

        [ObservableProperty]
        private DimensionTypeItem _selectedDimensionType;

        [ObservableProperty]
        private bool _useGridsForDimension = true;

        [ObservableProperty]
        private bool _useWallsForDimension = true;

        [ObservableProperty]
        private DimensionMode _selectedDimensionMode = DimensionMode.GridToOpenings;

        [ObservableProperty]
        private DimensionDirection _selectedDimensionDirection = DimensionDirection.Both;

        [ObservableProperty]
        private string _exportTrainingDataMessage;

        public IReadOnlyList<DimensionMode> AvailableDimensionModes { get; } = new[]
        {
            DimensionMode.GridToOpenings,
            DimensionMode.BetweenOpenings,
            DimensionMode.All
        };

        public IReadOnlyList<DimensionDirection> AvailableDimensionDirections { get; } = new[]
        {
            DimensionDirection.Both,
            DimensionDirection.Horizontal,
            DimensionDirection.Vertical
        };

        public IReadOnlyList<TagPosition> AvailablePositions { get; } = new[]
        {
            TagPosition.TopRight,
            TagPosition.TopLeft,
            TagPosition.TopCenter,
            TagPosition.Right,
            TagPosition.Left,
            TagPosition.BottomRight,
            TagPosition.BottomLeft,
            TagPosition.BottomCenter,
            TagPosition.Center,
            TagPosition.Auto
        };

        public SmartTagViewModel(ExternalEvent externalEvent, SmartTagHandler handler)
        {
            _externalEvent = externalEvent;
            _handler = handler;

            // Subscribe to handler callbacks
            _handler.OnCategoryStatsLoaded += HandleCategoryStatsLoaded;
            _handler.OnAutoTagCompleted += HandleAutoTagCompleted;
            _handler.OnPlacementsCalculated += HandlePlacementsCalculated;
            _handler.OnPreviewTagsCreated += HandlePreviewTagsCreated;
            _handler.OnError += HandleError;
            _handler.OnStatusUpdate += HandleStatusUpdate;
            _handler.OnAutoDimensionCompleted += HandleAutoDimensionCompleted;
            _handler.OnDimensionTypesLoaded += HandleDimensionTypesLoaded;
            _handler.OnExportTrainingDataCompleted += HandleExportTrainingDataCompleted;
        }
        
        /// <summary>
        /// Initialize and auto-load categories when window opens.
        /// Call this after window is shown.
        /// </summary>
        public void Initialize()
        {
            // Auto-load categories on startup
            RefreshCategories();
        }

        #region Commands

        [RelayCommand]
        private void RefreshCategories()
        {
            IsBusy = true;
            StatusMessage = "Loading categories...";
            _handler.SetRequest(SmartTagRequest.GetCategoryStats());
            _externalEvent.Raise();
        }

        [RelayCommand(CanExecute = nameof(CanExecuteAutoTag))]
        private void ExecuteAutoTag()
        {
            if (!CanExecuteAutoTag()) return;

            IsBusy = true;
            StatusMessage = "Executing auto-tag...";

            var settings = BuildSettings();
            _handler.SetRequest(SmartTagRequest.ExecuteAutoTag(settings));
            _externalEvent.Raise();
        }

        private bool CanExecuteAutoTag()
        {
            return !IsBusy && !IsPreviewMode && Categories.Any(c => c.IsSelected);
        }
        
        /// <summary>
        /// Create preview tags - user can review before confirming.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanCreatePreview))]
        private void CreatePreview()
        {
            if (!CanCreatePreview()) return;

            IsBusy = true;
            StatusMessage = "Creating preview tags...";
            IsPreviewMode = true;

            var settings = BuildSettings();
            _handler.SetRequest(SmartTagRequest.CreatePreviewTags(settings));
            _externalEvent.Raise();
        }

        private bool CanCreatePreview()
        {
            return !IsBusy && !IsPreviewMode && Categories.Any(c => c.IsSelected);
        }
        
        /// <summary>
        /// Confirm preview tags - keep them permanently.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanConfirmPreview))]
        private void ConfirmPreview()
        {
            if (!CanConfirmPreview()) return;

            IsBusy = true;
            StatusMessage = "Confirming tags...";

            _handler.SetRequest(SmartTagRequest.ConfirmPreviewTags());
            _externalEvent.Raise();
            
            IsPreviewMode = false;
            HasPreviewTags = false;
            IsBusy = false;
        }

        private bool CanConfirmPreview()
        {
            return !IsBusy && IsPreviewMode && HasPreviewTags;
        }
        
        /// <summary>
        /// Undo preview tags - delete all preview tags.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanUndoPreview))]
        private void UndoPreview()
        {
            if (!CanUndoPreview()) return;

            IsBusy = true;
            StatusMessage = "Undoing preview tags...";

            _handler.SetRequest(SmartTagRequest.UndoPreviewTags());
            _externalEvent.Raise();
            
            IsPreviewMode = false;
            HasPreviewTags = false;
            PreviewCreatedCount = 0;
            IsBusy = false;
        }

        private bool CanUndoPreview()
        {
            return !IsBusy && IsPreviewMode && HasPreviewTags;
        }

        /// <summary>
        /// Export training data from current view (for updating rules/patterns). Requires view with existing tags.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanExportTrainingData))]
        private void ExportTrainingData()
        {
            if (!CanExportTrainingData()) return;
            IsBusy = true;
            ExportTrainingDataMessage = "";
            StatusMessage = "Exporting training data from current view...";
            _handler.SetRequest(SmartTagRequest.ExportTrainingData());
            _externalEvent.Raise();
        }

        private bool CanExportTrainingData()
        {
            return !IsBusy && !IsPreviewMode;
        }

        [RelayCommand]
        private void SelectAll()
        {
            foreach (var cat in Categories)
            {
                cat.IsSelected = true;
            }
            UpdateCounts();
        }

        [RelayCommand]
        private void SelectNone()
        {
            foreach (var cat in Categories)
            {
                cat.IsSelected = false;
            }
            UpdateCounts();
        }

        [RelayCommand]
        private void SelectUntagged()
        {
            foreach (var cat in Categories)
            {
                // Select categories that have untagged elements
                cat.IsSelected = cat.ElementCount > cat.TaggedCount;
            }
            UpdateCounts();
        }

        [RelayCommand]
        private void PreviewPlacements()
        {
            if (!Categories.Any(c => c.IsSelected))
            {
                PreviewTagCount = 0;
                return;
            }

            var settings = BuildSettings();
            _handler.SetRequest(SmartTagRequest.PreviewPlacements(settings));
            _externalEvent.Raise();
        }

        [RelayCommand]
        private void LoadDimensionTypes()
        {
            _handler.SetRequest(SmartTagRequest.GetDimensionTypes());
            _externalEvent.Raise();
        }

        [RelayCommand]
        private void ExecuteAutoDimension()
        {
            IsBusy = true;
            StatusMessage = "Creating dimensions for openings...";

            var settings = BuildDimensionSettings();
            _handler.SetRequest(SmartTagRequest.ExecuteAutoDimension(settings));
            _externalEvent.Raise();
        }

        [RelayCommand]
        private void DimensionSelection()
        {
            IsBusy = true;
            StatusMessage = "Dimensioning selected elements...";

            var settings = BuildDimensionSettings();
            _handler.SetRequest(SmartTagRequest.DimensionSelection(settings, null)); // null = use current selection
            _externalEvent.Raise();
        }

        #endregion

        #region Callbacks

        private void HandleCategoryStatsLoaded(List<CategoryTagConfig> stats)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Categories.Clear();
                foreach (var stat in stats)
                {
                    var vm = new CategoryTagConfigViewModel(stat);
                    vm.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(CategoryTagConfigViewModel.IsSelected))
                        {
                            UpdateCounts();
                        }
                    };
                    Categories.Add(vm);
                }
                UpdateCounts();
                IsBusy = false;
                StatusMessage = $"Found {stats.Count} taggable categories";
            });
        }

        private void HandleAutoTagCompleted(TagResult result)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LastResult = result;
                IsBusy = false;

                var msg = $"Created {result.TagsCreated} tags";
                if (result.TagsSkipped > 0)
                    msg += $", skipped {result.TagsSkipped}";
                if (result.CollisionsResolved > 0)
                    msg += $", resolved {result.CollisionsResolved} collisions";
                msg += $" ({result.Duration.TotalSeconds:F1}s)";

                StatusMessage = msg;

                // Show summary if there were warnings
                if (result.Warnings.Count > 0)
                {
                    var warningMsg = $"Auto-tag completed with {result.Warnings.Count} warning(s):\n\n";
                    warningMsg += string.Join("\n", result.Warnings.Take(5));
                    if (result.Warnings.Count > 5)
                        warningMsg += $"\n... and {result.Warnings.Count - 5} more";

                    MessageBox.Show(warningMsg, "Smart Tag Results", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Refresh category stats
                RefreshCategories();
            });
        }

        private void HandlePlacementsCalculated(List<TagPlacement> placements, List<TaggableElement> elements)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                PreviewTagCount = placements.Count;
                TotalElementCount = elements.Count;
            });
        }
        
        private void HandlePreviewTagsCreated(TagResult result, bool hasPreviewTags)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LastResult = result;
                HasPreviewTags = hasPreviewTags;
                PreviewCreatedCount = result.TagsCreated;
                IsBusy = false;

                if (hasPreviewTags)
                {
                    StatusMessage = $"Preview: {result.TagsCreated} tags created. Review and Confirm or Undo.";
                }
                else
                {
                    StatusMessage = $"Preview failed: {string.Join(", ", result.Warnings.Take(2))}";
                    IsPreviewMode = false;
                }
                
                // Update command states
                ConfirmPreviewCommand.NotifyCanExecuteChanged();
                UndoPreviewCommand.NotifyCanExecuteChanged();
                ExecuteAutoTagCommand.NotifyCanExecuteChanged();
                CreatePreviewCommand.NotifyCanExecuteChanged();
            });
        }

        private void HandleError(string message)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                StatusMessage = $"Error: {message}";
            });
        }

        private void HandleStatusUpdate(string message)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusMessage = message;
            });
        }

        private void HandleExportTrainingDataCompleted(string outputPath, int sampleCount, bool success)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsBusy = false;
                if (success)
                {
                    ExportTrainingDataMessage = $"Exported {sampleCount} samples to: {outputPath}";
                    StatusMessage = ExportTrainingDataMessage;
                }
                else
                {
                    ExportTrainingDataMessage = "Export failed. Check that the view has tags.";
                    StatusMessage = ExportTrainingDataMessage;
                }
            });
        }

        private void HandleAutoDimensionCompleted(DimensionResult result)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LastDimensionResult = result;
                IsBusy = false;

                var msg = $"Created {result.DimensionsCreated} dimensions";
                if (result.GroupsProcessed > 0)
                    msg += $" ({result.GroupsProcessed} groups)";
                msg += $" in {result.Duration.TotalSeconds:F1}s";

                StatusMessage = msg;

                // Show warnings if any
                if (result.Warnings.Count > 0)
                {
                    var warningMsg = $"Auto-dimension completed with {result.Warnings.Count} warning(s):\n\n";
                    warningMsg += string.Join("\n", result.Warnings.Take(5));
                    if (result.Warnings.Count > 5)
                        warningMsg += $"\n... and {result.Warnings.Count - 5} more";

                    MessageBox.Show(warningMsg, "Smart Tag - Dimension Results",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
        }

        private void HandleDimensionTypesLoaded(List<(long Id, string Name)> types)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                DimensionTypes.Clear();
                DimensionTypes.Add(new DimensionTypeItem { Id = null, Name = "(Default)" });
                foreach (var t in types)
                {
                    DimensionTypes.Add(new DimensionTypeItem { Id = t.Id, Name = t.Name });
                }
                SelectedDimensionType = DimensionTypes.FirstOrDefault();
            });
        }

        #endregion

        #region Helpers

        private TagSettings BuildSettings()
        {
            var selectedCategories = Categories.Where(c => c.IsSelected).ToList();
            
            // Build per-category tag type overrides
            var categoryTagTypes = new Dictionary<long, long>();
            foreach (var cat in selectedCategories)
            {
                if (cat.SelectedTagType != null)
                {
                    categoryTagTypes[(long)cat.Category] = cat.SelectedTagType.TypeId;
                }
            }
            
            return new TagSettings
            {
                Categories = selectedCategories.Select(c => c.Category).ToList(),
                PreferredPosition = SelectedPosition,
                AddLeaders = AddLeaders,
                SkipTaggedElements = SkipTaggedElements,
                AlignTags = AlignTags,
                UseQuickMode = false,
                CategoryTagTypes = categoryTagTypes
            };
        }

        private void UpdateCounts()
        {
            SelectedCategoryCount = Categories.Count(c => c.IsSelected);
            TotalElementCount = Categories.Where(c => c.IsSelected).Sum(c => c.ElementCount);

            // Update preview
            if (SelectedCategoryCount > 0)
            {
                PreviewPlacements();
            }
            else
            {
                PreviewTagCount = 0;
            }

            ExecuteAutoTagCommand.NotifyCanExecuteChanged();
        }

        private DimensionSettings BuildDimensionSettings()
        {
            return new DimensionSettings
            {
                Mode = SelectedDimensionMode,
                Direction = SelectedDimensionDirection,
                UseGrids = UseGridsForDimension,
                UseWalls = UseWallsForDimension,
                DimensionTypeId = SelectedDimensionType?.Id
            };
        }

        public void Cleanup()
        {
            _handler.OnCategoryStatsLoaded -= HandleCategoryStatsLoaded;
            _handler.OnAutoTagCompleted -= HandleAutoTagCompleted;
            _handler.OnPlacementsCalculated -= HandlePlacementsCalculated;
            _handler.OnPreviewTagsCreated -= HandlePreviewTagsCreated;
            _handler.OnError -= HandleError;
            _handler.OnStatusUpdate -= HandleStatusUpdate;
            _handler.OnAutoDimensionCompleted -= HandleAutoDimensionCompleted;
            _handler.OnDimensionTypesLoaded -= HandleDimensionTypesLoaded;
            _handler.OnExportTrainingDataCompleted -= HandleExportTrainingDataCompleted;
        }

        #endregion
    }

    /// <summary>
    /// ViewModel wrapper for CategoryTagConfig for UI binding.
    /// </summary>
    public partial class CategoryTagConfigViewModel : ObservableObject
    {
        private readonly CategoryTagConfig _config;

        [ObservableProperty]
        private bool _isSelected;
        
        [ObservableProperty]
        private TagTypeInfo _selectedTagType;

        public BuiltInCategory Category => _config.Category;
        public string DisplayName => _config.DisplayName;
        public int ElementCount => _config.ElementCount;
        public int TaggedCount => _config.TaggedCount;
        public int UntaggedCount => ElementCount - TaggedCount;
        public List<TagTypeInfo> AvailableTagTypes => _config.AvailableTagTypes;
        public bool HasMultipleTagTypes => AvailableTagTypes?.Count > 1;
        
        public string DisplayText => $"{DisplayName} ({UntaggedCount}/{ElementCount})";

        public CategoryTagConfigViewModel(CategoryTagConfig config)
        {
            _config = config;
            _isSelected = config.IsSelected;
            _selectedTagType = config.SelectedTagType;
        }
    }

    /// <summary>
    /// Simple item for dimension type dropdown.
    /// </summary>
    public class DimensionTypeItem
    {
        public long? Id { get; set; }
        public string Name { get; set; }

        public override string ToString() => Name;
    }
}
