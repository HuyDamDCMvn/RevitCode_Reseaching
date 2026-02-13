using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SmartTag.Models
{
    /// <summary>
    /// Types of auto-dimension operations.
    /// </summary>
    public enum DimensionMode
    {
        /// <summary>
        /// Dimension from nearest grid/wall to opening centers (horizontal chain).
        /// </summary>
        GridToOpenings,

        /// <summary>
        /// Dimension between opening centers only.
        /// </summary>
        BetweenOpenings,

        /// <summary>
        /// Dimension opening sizes (width x height or diameter).
        /// </summary>
        OpeningSizes,

        /// <summary>
        /// All of the above.
        /// </summary>
        All
    }

    /// <summary>
    /// Direction for dimension placement.
    /// </summary>
    public enum DimensionDirection
    {
        Horizontal,
        Vertical,
        Both
    }

    /// <summary>
    /// Represents a dimensionable opening (wall/floor penetration).
    /// </summary>
    public class DimensionableOpening
    {
        public long ElementId { get; set; }
        public string OpeningType { get; set; } // BDB_RU, BDB_RE, WDB_RU, WDB_RE
        public bool IsCircular { get; set; }
        public bool IsFloorOpening { get; set; } // true = floor (BDB), false = wall (WDB)
        
        /// <summary>
        /// Center point in model coordinates.
        /// </summary>
        public XYZ CenterPoint { get; set; }
        
        /// <summary>
        /// Center point in view coordinates (2D).
        /// </summary>
        public Point2D ViewCenter { get; set; }
        
        /// <summary>
        /// Opening dimensions (Width, Height for rectangular; Diameter for circular).
        /// </summary>
        public double Width { get; set; }
        public double Height { get; set; }
        public double Diameter { get; set; }
        
        /// <summary>
        /// Bottom elevation (DB UK).
        /// </summary>
        public double BottomElevation { get; set; }
        
        /// <summary>
        /// System name (SN).
        /// </summary>
        public string SystemName { get; set; }
        
        /// <summary>
        /// Reference for dimensioning.
        /// </summary>
        public Reference CenterReference { get; set; }
        
        /// <summary>
        /// Left/Right edge references for width dimension.
        /// </summary>
        public Reference LeftReference { get; set; }
        public Reference RightReference { get; set; }
        
        /// <summary>
        /// Top/Bottom edge references for height dimension.
        /// </summary>
        public Reference TopReference { get; set; }
        public Reference BottomReference { get; set; }
    }

    /// <summary>
    /// Represents a grid line or reference wall for dimensioning.
    /// </summary>
    public class DimensionReference
    {
        public long ElementId { get; set; }
        public string Name { get; set; }
        public bool IsGrid { get; set; }
        public bool IsWall { get; set; }
        
        /// <summary>
        /// Line geometry in view coordinates.
        /// </summary>
        public Line ViewLine { get; set; }
        
        /// <summary>
        /// Reference for dimensioning.
        /// </summary>
        public Reference Reference { get; set; }
        
        /// <summary>
        /// Position along perpendicular axis (for sorting).
        /// </summary>
        public double Position { get; set; }
    }

    /// <summary>
    /// A group of openings to dimension together (same row/column).
    /// </summary>
    public class OpeningGroup
    {
        public List<DimensionableOpening> Openings { get; set; } = new List<DimensionableOpening>();
        public DimensionDirection Direction { get; set; }
        
        /// <summary>
        /// The common coordinate (Y for horizontal row, X for vertical column).
        /// </summary>
        public double CommonCoordinate { get; set; }
        
        /// <summary>
        /// Nearest grid/wall reference at the start.
        /// </summary>
        public DimensionReference StartReference { get; set; }
        
        /// <summary>
        /// Nearest grid/wall reference at the end.
        /// </summary>
        public DimensionReference EndReference { get; set; }
    }

    /// <summary>
    /// Settings for auto-dimensioning operation.
    /// </summary>
    public class DimensionSettings
    {
        /// <summary>
        /// What to dimension.
        /// </summary>
        public DimensionMode Mode { get; set; } = DimensionMode.GridToOpenings;

        /// <summary>
        /// Direction(s) to create dimensions.
        /// </summary>
        public DimensionDirection Direction { get; set; } = DimensionDirection.Both;

        /// <summary>
        /// Include grid lines as references.
        /// </summary>
        public bool UseGrids { get; set; } = true;

        /// <summary>
        /// Include walls as references.
        /// </summary>
        public bool UseWalls { get; set; } = true;

        /// <summary>
        /// Tolerance for grouping openings into rows/columns (in feet).
        /// </summary>
        public double GroupingTolerance { get; set; } = 1.0; // 1 foot

        /// <summary>
        /// Offset distance from elements for dimension line placement (in feet).
        /// </summary>
        public double DimensionOffset { get; set; } = 2.0; // 2 feet

        /// <summary>
        /// Minimum number of openings in a group to create dimensions.
        /// </summary>
        public int MinOpeningsPerGroup { get; set; } = 1;

        /// <summary>
        /// Dimension type to use (null = default).
        /// </summary>
        public long? DimensionTypeId { get; set; }

        /// <summary>
        /// Categories to include for openings.
        /// </summary>
        public List<BuiltInCategory> OpeningCategories { get; set; } = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_FloorOpening,
            BuiltInCategory.OST_ShaftOpening,
            BuiltInCategory.OST_SWallRectOpening,
            BuiltInCategory.OST_ArcWallRectOpening,
            BuiltInCategory.OST_GenericModel // Often used for custom openings
        };
    }

    /// <summary>
    /// Result of auto-dimensioning operation.
    /// </summary>
    public class DimensionResult
    {
        public int DimensionsCreated { get; set; }
        public int GroupsProcessed { get; set; }
        public int OpeningsProcessed { get; set; }
        public int Skipped { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<long> CreatedDimensionIds { get; set; } = new List<long>();
        public TimeSpan Duration { get; set; }
    }
}
