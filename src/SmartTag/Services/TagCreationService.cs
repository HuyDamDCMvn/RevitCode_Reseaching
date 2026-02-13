using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Service for creating tags in Revit.
    /// </summary>
    public class TagCreationService
    {
        private readonly Document _doc;
        private readonly View _view;

        public TagCreationService(Document doc, View view)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <summary>
        /// Create tags based on calculated placements.
        /// Must be called within a transaction.
        /// </summary>
        public TagResult CreateTags(List<TagPlacement> placements, TagSettings settings)
        {
            var result = new TagResult();
            var startTime = DateTime.Now;

            if (placements == null || settings == null)
                return result;

            foreach (var placement in placements)
            {
                try
                {
                    var tag = CreateTag(placement, settings);
                    if (tag != null)
                    {
                        result.TagsCreated++;
                        result.CreatedTagIds.Add(tag.Id.Value);
                    }
                    else
                    {
                        result.TagsSkipped++;
                        result.Warnings.Add($"Could not create tag for element {placement.ElementId}");
                    }
                }
                catch (Exception ex)
                {
                    result.TagsSkipped++;
                    result.Warnings.Add($"Error tagging element {placement.ElementId}: {ex.Message}");
                }
            }

            result.Duration = DateTime.Now - startTime;
            return result;
        }

        /// <summary>
        /// Create a single tag for an element.
        /// </summary>
        private IndependentTag CreateTag(TagPlacement placement, TagSettings settings)
        {
            if (placement == null || settings == null) return null;
            
            try
            {
                var elementId = new ElementId(placement.ElementId);
                var element = _doc.GetElement(elementId);
                if (element == null) return null;
                
                // Validate element can be tagged
                if (element.Category == null) return null;

                // Get tag location in 3D (convert from view 2D coordinates)
                var tagPoint = ConvertToModelPoint(placement.TagLocation);
                
                // Validate point
                if (tagPoint == null || double.IsNaN(tagPoint.X) || double.IsNaN(tagPoint.Y) || double.IsNaN(tagPoint.Z))
                    return null;

                // Get reference to the element
                Reference reference;
                try
                {
                    reference = new Reference(element);
                }
                catch
                {
                    return null; // Element cannot be referenced
                }

                // Determine tag type - check per-category override first
                FamilySymbol tagType = null;
                var categoryId = (long)element.Category.Id.Value;
                
                // Check per-category tag type override
                if (settings.CategoryTagTypes != null && 
                    settings.CategoryTagTypes.TryGetValue(categoryId, out var categoryTagTypeId))
                {
                    tagType = _doc.GetElement(new ElementId(categoryTagTypeId)) as FamilySymbol;
                }
                
                // Fallback to global tag type
                if (tagType == null && settings.TagTypeId.HasValue)
                {
                    tagType = _doc.GetElement(new ElementId(settings.TagTypeId.Value)) as FamilySymbol;
                }

                // Find default tag type if not specified
                if (tagType == null)
                {
                    tagType = GetDefaultTagType(element.Category);
                }

                if (tagType == null)
                {
                    return null; // No suitable tag type found
                }

                // Ensure tag type is active
                if (!tagType.IsActive)
                {
                    try
                    {
                        tagType.Activate();
                        _doc.Regenerate(); // May be needed for activation
                    }
                    catch
                    {
                        // Activation failed, try anyway
                    }
                }

                // #10: Determine tag orientation based on placement rotation
                var orientation = placement.Rotation == TagRotation.Vertical 
                    ? TagOrientation.Vertical 
                    : TagOrientation.Horizontal;

                // Create the tag
                IndependentTag tag = null;

                try
                {
                    tag = IndependentTag.Create(
                        _doc,
                        tagType.Id,
                        _view.Id,
                        reference,
                        settings.AddLeaders && placement.HasLeader,
                        orientation,
                        tagPoint);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    // Element cannot be tagged in this view (e.g., not visible)
                    return null;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Invalid argument (e.g., tag type doesn't match element category)
                    return null;
                }

                // Adjust leader end point if needed
                if (tag != null && settings.AddLeaders && placement.HasLeader)
                {
                    try
                    {
                        var leaderEnd = ConvertToModelPoint(placement.LeaderEnd);
                        if (leaderEnd != null && !double.IsNaN(leaderEnd.X))
                        {
                            tag.LeaderEndCondition = LeaderEndCondition.Free;
                            tag.SetLeaderEnd(reference, leaderEnd);
                        }
                    }
                    catch
                    {
                        // Leader adjustment failed - not critical
                    }
                }

                return tag;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating tag for element {placement?.ElementId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Convert 2D view coordinates to 3D model coordinates.
        /// </summary>
        private XYZ ConvertToModelPoint(Point2D viewPoint)
        {
            var origin = _view.Origin;
            var rightDir = _view.RightDirection;
            var upDir = _view.UpDirection;

            return origin + rightDir * viewPoint.X + upDir * viewPoint.Y;
        }

        /// <summary>
        /// Get the default tag type for a category.
        /// </summary>
        private FamilySymbol GetDefaultTagType(Category category)
        {
            if (category == null) return null;

            // Map from element category to tag category
            var tagCategory = GetTagCategoryForElementCategory(category);
            if (tagCategory == BuiltInCategory.INVALID) return null;

            // Find tag types
            var collector = new FilteredElementCollector(_doc)
                .OfCategory(tagCategory)
                .OfClass(typeof(FamilySymbol))
                .WhereElementIsElementType();

            foreach (FamilySymbol symbol in collector)
            {
                // Return first available tag type
                return symbol;
            }

            return null;
        }

        /// <summary>
        /// Map element category to its corresponding tag category.
        /// </summary>
        private BuiltInCategory GetTagCategoryForElementCategory(Category category)
        {
            // Common mappings
            var mappings = new Dictionary<BuiltInCategory, BuiltInCategory>
            {
                { BuiltInCategory.OST_Walls, BuiltInCategory.OST_WallTags },
                { BuiltInCategory.OST_Doors, BuiltInCategory.OST_DoorTags },
                { BuiltInCategory.OST_Windows, BuiltInCategory.OST_WindowTags },
                { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_RoomTags },
                { BuiltInCategory.OST_Areas, BuiltInCategory.OST_AreaTags },
                { BuiltInCategory.OST_Floors, BuiltInCategory.OST_FloorTags },
                { BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_CeilingTags },
                { BuiltInCategory.OST_Roofs, BuiltInCategory.OST_RoofTags },
                { BuiltInCategory.OST_Columns, BuiltInCategory.OST_ColumnTags },
                { BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralColumnTags },
                { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingTags },
                { BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralFoundationTags },
                { BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureTags },
                { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_GenericModelTags },
                { BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags },
                { BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalEquipmentTags },
                { BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalFixtureTags },
                { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingFixtureTags },
                { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PlumbingFixtureTags },
                { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctTags },
                { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeTags },
                { BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctAccessoryTags },
                { BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctFittingTags },
                { BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctTerminalTags },
                { BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PipeAccessoryTags },
                { BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeFittingTags },
                { BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_SprinklerTags },
                { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayTags },
                { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitTags },
                { BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_SpecialityEquipmentTags }
            };

            try
            {
                var catId = (BuiltInCategory)category.Id.Value;
                if (mappings.TryGetValue(catId, out var tagCat))
                {
                    return tagCat;
                }
            }
            catch
            {
                // Not a built-in category
            }

            // Try generic annotation
            return BuiltInCategory.OST_GenericAnnotation;
        }

        /// <summary>
        /// Get all available tag types for a specific element.
        /// </summary>
        public List<(long TypeId, string Name)> GetAvailableTagTypes(Element element)
        {
            var result = new List<(long, string)>();
            if (element?.Category == null) return result;

            var tagCategory = GetTagCategoryForElementCategory(element.Category);
            if (tagCategory == BuiltInCategory.INVALID) return result;

            var collector = new FilteredElementCollector(_doc)
                .OfCategory(tagCategory)
                .OfClass(typeof(FamilySymbol))
                .WhereElementIsElementType();

            foreach (FamilySymbol symbol in collector)
            {
                var name = $"{symbol.Family?.Name ?? "Unknown"}: {symbol.Name}";
                result.Add((symbol.Id.Value, name));
            }

            return result;
        }
    }
}
