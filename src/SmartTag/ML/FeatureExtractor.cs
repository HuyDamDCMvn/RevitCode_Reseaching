using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SmartTag.Models;

namespace SmartTag.ML
{
    /// <summary>
    /// Extracts feature vectors from elements for KNN matching and RL.
    /// </summary>
    public class FeatureExtractor
    {
        // Feature vector dimensions
        public const int CATEGORY_DIMS = 8;      // One-hot encoded categories
        public const int GEOMETRY_DIMS = 5;      // Orientation, length, width, height, diameter
        public const int CONTEXT_DIMS = 7;       // Density, neighbors, distance to wall
        public const int TOTAL_DIMS = 20;        // Total feature dimensions

        // Category encoding
        private static readonly Dictionary<string, int> CategoryIndex = new()
        {
            { "OST_PipeCurves", 0 },
            { "OST_DuctCurves", 1 },
            { "OST_CableTray", 2 },
            { "OST_Conduit", 3 },
            { "OST_MechanicalEquipment", 4 },
            { "OST_ElectricalEquipment", 5 },
            { "OST_PlumbingFixtures", 6 },
            { "Other", 7 }
        };

        // Normalization parameters (from training data statistics)
        private const double MAX_LENGTH = 100.0;   // feet
        private const double MAX_WIDTH = 20.0;     // feet
        private const double MAX_HEIGHT = 20.0;    // feet
        private const double MAX_DIAMETER = 5.0;   // feet
        private const double MAX_DISTANCE = 50.0;  // feet

        /// <summary>
        /// Extract feature vector from a taggable element.
        /// </summary>
        public float[] ExtractFeatures(TaggableElement element, ElementContext context)
        {
            var features = new float[TOTAL_DIMS];
            int idx = 0;

            // 1. Category one-hot encoding (dims 0-7)
            var catIdx = GetCategoryIndex(element.BuiltInCategoryName);
            for (int i = 0; i < CATEGORY_DIMS; i++)
            {
                features[idx++] = (i == catIdx) ? 1.0f : 0.0f;
            }

            // 2. Geometry features (dims 8-12)
            features[idx++] = NormalizeAngle(element.AngleRadians);
            features[idx++] = Normalize(element.LengthFeet, MAX_LENGTH);
            features[idx++] = Normalize(element.ViewBounds.Width, MAX_WIDTH);
            features[idx++] = Normalize(element.ViewBounds.Height, MAX_HEIGHT);
            features[idx++] = element.IsLinearElement ? 1.0f : 0.0f;

            // 3. Context features (dims 13-19)
            features[idx++] = EncodeDensity(context.Density);
            features[idx++] = context.HasNeighborAbove ? 1.0f : 0.0f;
            features[idx++] = context.HasNeighborBelow ? 1.0f : 0.0f;
            features[idx++] = context.HasNeighborLeft ? 1.0f : 0.0f;
            features[idx++] = context.HasNeighborRight ? 1.0f : 0.0f;
            features[idx++] = Normalize(context.DistanceToWall, MAX_DISTANCE);
            features[idx++] = context.ParallelElementsCount > 0 ? 
                Normalize(context.ParallelElementsCount, 10) : 0.0f;

            return features;
        }

        /// <summary>
        /// Extract feature vector from raw element data (for training).
        /// </summary>
        public float[] ExtractFeaturesFromRaw(
            string category,
            double orientation,
            double length,
            double width,
            double height,
            bool isLinear,
            string density,
            bool neighborAbove,
            bool neighborBelow,
            bool neighborLeft,
            bool neighborRight,
            double distanceToWall,
            int parallelCount)
        {
            var features = new float[TOTAL_DIMS];
            int idx = 0;

            // Category one-hot
            var catIdx = GetCategoryIndex(category);
            for (int i = 0; i < CATEGORY_DIMS; i++)
            {
                features[idx++] = (i == catIdx) ? 1.0f : 0.0f;
            }

            // Geometry
            features[idx++] = NormalizeAngle(orientation * Math.PI / 180.0);
            features[idx++] = Normalize(length, MAX_LENGTH);
            features[idx++] = Normalize(width, MAX_WIDTH);
            features[idx++] = Normalize(height, MAX_HEIGHT);
            features[idx++] = isLinear ? 1.0f : 0.0f;

            // Context
            features[idx++] = EncodeDensity(ParseDensity(density));
            features[idx++] = neighborAbove ? 1.0f : 0.0f;
            features[idx++] = neighborBelow ? 1.0f : 0.0f;
            features[idx++] = neighborLeft ? 1.0f : 0.0f;
            features[idx++] = neighborRight ? 1.0f : 0.0f;
            features[idx++] = Normalize(distanceToWall, MAX_DISTANCE);
            features[idx++] = Normalize(parallelCount, 10);

            return features;
        }

        #region Helpers

        private int GetCategoryIndex(string category)
        {
            if (string.IsNullOrEmpty(category))
                return CategoryIndex["Other"];

            // Try exact match
            if (CategoryIndex.TryGetValue(category, out int idx))
                return idx;

            // Try partial match for fittings/accessories
            if (category.Contains("Pipe"))
                return CategoryIndex["OST_PipeCurves"];
            if (category.Contains("Duct"))
                return CategoryIndex["OST_DuctCurves"];
            if (category.Contains("CableTray"))
                return CategoryIndex["OST_CableTray"];
            if (category.Contains("Conduit"))
                return CategoryIndex["OST_Conduit"];
            if (category.Contains("Mechanical"))
                return CategoryIndex["OST_MechanicalEquipment"];
            if (category.Contains("Electrical"))
                return CategoryIndex["OST_ElectricalEquipment"];
            if (category.Contains("Plumbing") || category.Contains("Fixture"))
                return CategoryIndex["OST_PlumbingFixtures"];

            return CategoryIndex["Other"];
        }

        private float NormalizeAngle(double radians)
        {
            // Normalize to 0-1 range
            var normalized = (radians % (2 * Math.PI)) / (2 * Math.PI);
            return (float)Math.Max(0, Math.Min(1, normalized));
        }

        private float Normalize(double value, double max)
        {
            if (max <= 0) return 0;
            return (float)Math.Max(0, Math.Min(1, value / max));
        }

        private float EncodeDensity(DensityLevel density)
        {
            return density switch
            {
                DensityLevel.Low => 0.0f,
                DensityLevel.Medium => 0.5f,
                DensityLevel.High => 1.0f,
                _ => 0.5f
            };
        }

        private DensityLevel ParseDensity(string density)
        {
            return density?.ToLower() switch
            {
                "low" => DensityLevel.Low,
                "medium" => DensityLevel.Medium,
                "high" => DensityLevel.High,
                _ => DensityLevel.Medium
            };
        }

        #endregion

        #region Feature Names (for debugging)

        public static string[] GetFeatureNames()
        {
            return new[]
            {
                "cat_pipe", "cat_duct", "cat_cabletray", "cat_conduit",
                "cat_mech_equip", "cat_elec_equip", "cat_plumbing", "cat_other",
                "orientation", "length", "width", "height", "is_linear",
                "density", "neighbor_above", "neighbor_below", 
                "neighbor_left", "neighbor_right", "dist_to_wall", "parallel_count"
            };
        }

        #endregion
    }

    /// <summary>
    /// Context information about an element's surroundings.
    /// </summary>
    public class ElementContext
    {
        public DensityLevel Density { get; set; }
        public int NeighborCount { get; set; }
        public bool HasNeighborAbove { get; set; }
        public bool HasNeighborBelow { get; set; }
        public bool HasNeighborLeft { get; set; }
        public bool HasNeighborRight { get; set; }
        public double DistanceToNearestAbove { get; set; }
        public double DistanceToNearestBelow { get; set; }
        public double DistanceToNearestLeft { get; set; }
        public double DistanceToNearestRight { get; set; }
        public double DistanceToWall { get; set; }
        public int ParallelElementsCount { get; set; }
        public bool IsInGroup { get; set; }
    }

    public enum DensityLevel
    {
        Low,
        Medium,
        High
    }
}
