using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CommonFeature.Models
{
    /// <summary>
    /// Settings for boundary display visualization.
    /// Used to pass display preferences from UI to graphics server.
    /// </summary>
    public class BoundaryDisplaySettings
    {
        /// <summary>
        /// List of element IDs to visualize boundaries for.
        /// </summary>
        public List<long> ElementIds { get; set; } = new();
        
        /// <summary>
        /// Whether to show bounding box wireframe.
        /// </summary>
        public bool ShowBoundingBox { get; set; }
        
        /// <summary>
        /// If true, rotate bounding box to align with element orientation.
        /// If false, use world-aligned (axis-aligned) bounding box.
        /// </summary>
        public bool UseRotatedBoundingBox { get; set; }
        
        /// <summary>
        /// Whether to show minimum point sphere.
        /// </summary>
        public bool ShowMinPoint { get; set; }
        
        /// <summary>
        /// Whether to show maximum point sphere.
        /// </summary>
        public bool ShowMaxPoint { get; set; }
        
        /// <summary>
        /// Whether to show centroid point sphere.
        /// </summary>
        public bool ShowCentroid { get; set; }
        
        /// <summary>
        /// Color for bounding box wireframe lines.
        /// </summary>
        public Color BoundingBoxColor { get; set; }
        
        /// <summary>
        /// Color for minimum point sphere.
        /// </summary>
        public Color MinPointColor { get; set; }
        
        /// <summary>
        /// Color for maximum point sphere.
        /// </summary>
        public Color MaxPointColor { get; set; }
        
        /// <summary>
        /// Color for centroid point sphere.
        /// </summary>
        public Color CentroidColor { get; set; }
        
        /// <summary>
        /// Line thickness for bounding box (1-10).
        /// Note: DirectContext3D may not support variable line thickness.
        /// </summary>
        public int LineThickness { get; set; } = 2;
        
        /// <summary>
        /// Diameter of point spheres in millimeters (20-500).
        /// </summary>
        public int SphereDiameterMm { get; set; } = 100;
    }

    /// <summary>
    /// Calculated boundary data for a single element.
    /// Contains geometric information extracted from Revit element.
    /// </summary>
    public class ElementBoundaryData
    {
        /// <summary>
        /// Element ID (long value, not ElementId object).
        /// </summary>
        public long ElementId { get; set; }
        
        /// <summary>
        /// World-aligned bounding box of the element.
        /// </summary>
        public BoundingBoxXYZ BoundingBox { get; set; }
        
        /// <summary>
        /// Rotation transform for oriented bounding box visualization.
        /// Null if element has no rotation or rotation couldn't be determined.
        /// </summary>
        public Transform RotationTransform { get; set; }
        
        /// <summary>
        /// Minimum corner point of the bounding box.
        /// </summary>
        public XYZ MinPoint { get; set; }
        
        /// <summary>
        /// Maximum corner point of the bounding box.
        /// </summary>
        public XYZ MaxPoint { get; set; }
        
        /// <summary>
        /// Center point of the bounding box.
        /// </summary>
        public XYZ Centroid { get; set; }
    }
}
