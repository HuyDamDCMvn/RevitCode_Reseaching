using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SmartTag.Models
{
    /// <summary>
    /// Represents tag placement position preference.
    /// </summary>
    public enum TagPosition
    {
        TopRight,
        TopLeft,
        TopCenter,
        BottomRight,
        BottomLeft,
        BottomCenter,
        Left,
        Right,
        Center,
        Auto // AI decides
    }

    /// <summary>
    /// Represents a taggable element with its bounding box in view coordinates.
    /// </summary>
    public class TaggableElement
    {
        public long ElementId { get; set; }
        public string CategoryName { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        
        /// <summary>
        /// Bounding box in view coordinates (2D).
        /// </summary>
        public BoundingBox2D ViewBounds { get; set; }
        
        /// <summary>
        /// Center point in view coordinates.
        /// </summary>
        public Point2D Center { get; set; }
        
        /// <summary>
        /// Whether this element already has a tag.
        /// </summary>
        public bool HasExistingTag { get; set; }
        
        /// <summary>
        /// Existing tag ID if any.
        /// </summary>
        public long? ExistingTagId { get; set; }

        #region MEP System Info (for rule-based tagging)

        /// <summary>
        /// Revit BuiltInCategory enum value as string (e.g., "OST_PipeCurves")
        /// </summary>
        public string BuiltInCategoryName { get; set; }

        /// <summary>
        /// MEP System Name from Revit (e.g., "HZG-HK-VL", "SW", "KLT-IT-VL")
        /// </summary>
        public string SystemName { get; set; }

        /// <summary>
        /// System classification (e.g., "SanitaryWaste", "DomesticColdWater", "SupplyAir")
        /// </summary>
        public string SystemClassification { get; set; }

        /// <summary>
        /// Pipe/duct diameter in mm (e.g., 100 for DN100)
        /// </summary>
        public double? SizeMM { get; set; }

        /// <summary>
        /// Formatted size string (e.g., "DN100", "400x300")
        /// </summary>
        public string SizeString { get; set; }

        /// <summary>
        /// Elevation in meters relative to level
        /// </summary>
        public double? ElevationM { get; set; }

        /// <summary>
        /// Pipe slope as decimal (0.01 = 1%)
        /// </summary>
        public double? Slope { get; set; }

        /// <summary>
        /// Pre-generated tag text based on rules (if calculated)
        /// </summary>
        public string GeneratedTagText { get; set; }

        /// <summary>
        /// Rule ID used to generate tag text
        /// </summary>
        public string MatchedRuleId { get; set; }

        #endregion

        #region Linear Element Info (for pipes/ducts)

        /// <summary>
        /// Whether this is a linear element (pipe, duct, cable tray).
        /// </summary>
        public bool IsLinearElement { get; set; }

        /// <summary>
        /// Length of linear element in feet.
        /// </summary>
        public double LengthFeet { get; set; }

        /// <summary>
        /// Angle of linear element in radians (0 = horizontal right, PI/2 = up).
        /// </summary>
        public double AngleRadians { get; set; }

        /// <summary>
        /// Start point in view coordinates.
        /// </summary>
        public Point2D? StartPoint { get; set; }

        /// <summary>
        /// End point in view coordinates.
        /// </summary>
        public Point2D? EndPoint { get; set; }

        /// <summary>
        /// Whether element is in a Group (may require special handling).
        /// </summary>
        public bool IsInGroup { get; set; }

        /// <summary>
        /// Group ID if element is in a group.
        /// </summary>
        public long? GroupId { get; set; }

        #endregion
    }

    /// <summary>
    /// 2D point for view coordinates.
    /// </summary>
    public struct Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Point2D operator +(Point2D a, Point2D b) => new Point2D(a.X + b.X, a.Y + b.Y);
        public static Point2D operator -(Point2D a, Point2D b) => new Point2D(a.X - b.X, a.Y - b.Y);
        public static Point2D operator *(Point2D p, double s) => new Point2D(p.X * s, p.Y * s);
        
        public double DistanceTo(Point2D other)
        {
            var dx = X - other.X;
            var dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>
    /// 2D bounding box for view coordinates.
    /// </summary>
    public struct BoundingBox2D
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public Point2D Center => new Point2D((MinX + MaxX) / 2, (MinY + MaxY) / 2);
        public double Area => Width * Height;

        public BoundingBox2D(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        /// <summary>
        /// Check if this box intersects with another.
        /// </summary>
        public bool Intersects(BoundingBox2D other)
        {
            return !(MaxX < other.MinX || MinX > other.MaxX ||
                     MaxY < other.MinY || MinY > other.MaxY);
        }

        /// <summary>
        /// Check if this box contains a point.
        /// </summary>
        public bool Contains(Point2D point)
        {
            return point.X >= MinX && point.X <= MaxX &&
                   point.Y >= MinY && point.Y <= MaxY;
        }
        
        /// <summary>
        /// Check if this box fully contains another box.
        /// </summary>
        public bool Contains(BoundingBox2D other)
        {
            return MinX <= other.MinX && MaxX >= other.MaxX &&
                   MinY <= other.MinY && MaxY >= other.MaxY;
        }

        /// <summary>
        /// Expand the box by a margin.
        /// </summary>
        public BoundingBox2D Expand(double margin)
        {
            return new BoundingBox2D(
                MinX - margin,
                MinY - margin,
                MaxX + margin,
                MaxY + margin);
        }

        /// <summary>
        /// Get union with another box.
        /// </summary>
        public BoundingBox2D Union(BoundingBox2D other)
        {
            return new BoundingBox2D(
                Math.Min(MinX, other.MinX),
                Math.Min(MinY, other.MinY),
                Math.Max(MaxX, other.MaxX),
                Math.Max(MaxY, other.MaxY));
        }
    }

    /// <summary>
    /// Tag orientation (rotation).
    /// </summary>
    public enum TagRotation
    {
        /// <summary>
        /// Horizontal (3 o'clock direction, 0°).
        /// </summary>
        Horizontal = 0,
        
        /// <summary>
        /// Vertical (12 o'clock direction, 90°).
        /// </summary>
        Vertical = 90
    }

    /// <summary>
    /// Represents a proposed tag placement.
    /// </summary>
    public class TagPlacement
    {
        public long ElementId { get; set; }
        public Point2D TagLocation { get; set; }
        public Point2D LeaderEnd { get; set; }
        public bool HasLeader { get; set; }
        public TagPosition Position { get; set; }
        public BoundingBox2D EstimatedTagBounds { get; set; }
        
        /// <summary>
        /// Score for this placement (lower is better).
        /// </summary>
        public double Score { get; set; }
        
        /// <summary>
        /// Tag rotation (0° = horizontal, 90° = vertical).
        /// </summary>
        public TagRotation Rotation { get; set; } = TagRotation.Horizontal;
        
        /// <summary>
        /// Segment index for linear elements with multiple tags.
        /// </summary>
        public int SegmentIndex { get; set; }
        
        /// <summary>
        /// Distance multiplier used for this position (higher = farther from element).
        /// </summary>
        public double DistanceMultiplier { get; set; } = 1.0;
    }

    /// <summary>
    /// Settings for auto-tagging operation.
    /// </summary>
    public class TagSettings
    {
        /// <summary>
        /// Categories to tag.
        /// </summary>
        public List<BuiltInCategory> Categories { get; set; } = new List<BuiltInCategory>();

        /// <summary>
        /// Preferred tag position.
        /// </summary>
        public TagPosition PreferredPosition { get; set; } = TagPosition.TopRight;

        /// <summary>
        /// Whether to add leader lines.
        /// </summary>
        public bool AddLeaders { get; set; } = true;

        /// <summary>
        /// Minimum distance between tags (in view coordinates).
        /// </summary>
        public double MinTagDistance { get; set; } = 0.5; // feet

        /// <summary>
        /// Whether to skip elements that already have tags.
        /// </summary>
        public bool SkipTaggedElements { get; set; } = true;

        /// <summary>
        /// Whether to align tags in rows/columns.
        /// </summary>
        public bool AlignTags { get; set; } = true;

        /// <summary>
        /// Tag type ID to use (null = default).
        /// </summary>
        public long? TagTypeId { get; set; }
        
        /// <summary>
        /// Per-category tag type overrides (CategoryId -> TagTypeId).
        /// </summary>
        public Dictionary<long, long> CategoryTagTypes { get; set; } = new Dictionary<long, long>();

        /// <summary>
        /// Use Quick Mode (local heuristics) or Full Mode (cloud AI).
        /// </summary>
        public bool UseQuickMode { get; set; } = true;
    }

    /// <summary>
    /// Result of auto-tagging operation.
    /// </summary>
    public class TagResult
    {
        public int TagsCreated { get; set; }
        public int TagsSkipped { get; set; }
        public int CollisionsResolved { get; set; }
        public int ElementsSkippedNoSpace { get; set; } // Skipped due to no valid placement
        public int ElementsAlreadyTagged { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<long> CreatedTagIds { get; set; } = new List<long>();
        public List<long> SkippedElementIds { get; set; } = new List<long>(); // For user review
        public TimeSpan Duration { get; set; }
        
        /// <summary>
        /// Summary message for UI display.
        /// </summary>
        public string Summary
        {
            get
            {
                var parts = new List<string> { $"Created {TagsCreated} tags" };
                if (ElementsAlreadyTagged > 0) parts.Add($"{ElementsAlreadyTagged} already tagged");
                if (ElementsSkippedNoSpace > 0) parts.Add($"{ElementsSkippedNoSpace} skipped (no space)");
                if (CollisionsResolved > 0) parts.Add($"{CollisionsResolved} collisions resolved");
                return string.Join(", ", parts);
            }
        }
    }

    /// <summary>
    /// Category configuration for tagging.
    /// </summary>
    public class CategoryTagConfig
    {
        public BuiltInCategory Category { get; set; }
        public string DisplayName { get; set; }
        public bool IsSelected { get; set; }
        public int ElementCount { get; set; }
        public int TaggedCount { get; set; }
        public long? PreferredTagTypeId { get; set; }
        
        /// <summary>
        /// Available tag types for this category.
        /// </summary>
        public List<TagTypeInfo> AvailableTagTypes { get; set; } = new List<TagTypeInfo>();
        
        /// <summary>
        /// Selected tag type for this category.
        /// </summary>
        public TagTypeInfo SelectedTagType { get; set; }
    }
    
    /// <summary>
    /// Information about a tag type (Family + Type).
    /// </summary>
    public class TagTypeInfo
    {
        public long TypeId { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        
        /// <summary>
        /// Display name: "FamilyName: TypeName"
        /// </summary>
        public string DisplayName => string.IsNullOrEmpty(FamilyName) 
            ? TypeName 
            : $"{FamilyName}: {TypeName}";
            
        public override string ToString() => DisplayName;
    }
}
