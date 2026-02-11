using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace HDRevitLib
{
    /// <summary>
    /// Utility class for geometry operations in Revit.
    /// </summary>
    public static class Geometry
    {
        #region Constants

        /// <summary>
        /// Tolerance for geometric comparisons (approximately 1mm).
        /// </summary>
        public const double Tolerance = 0.001;

        /// <summary>
        /// Conversion factor: feet to millimeters.
        /// </summary>
        public const double FeetToMm = 304.8;

        /// <summary>
        /// Conversion factor: millimeters to feet.
        /// </summary>
        public const double MmToFeet = 1.0 / 304.8;

        /// <summary>
        /// Conversion factor: feet to meters.
        /// </summary>
        public const double FeetToMeters = 0.3048;

        /// <summary>
        /// Conversion factor: meters to feet.
        /// </summary>
        public const double MetersToFeet = 1.0 / 0.3048;

        #endregion

        #region Unit Conversion

        /// <summary>
        /// Convert feet to millimeters.
        /// </summary>
        public static double ToMm(double feet) => feet * FeetToMm;

        /// <summary>
        /// Convert millimeters to feet.
        /// </summary>
        public static double ToFeet(double mm) => mm * MmToFeet;

        /// <summary>
        /// Convert feet to meters.
        /// </summary>
        public static double ToMeters(double feet) => feet * FeetToMeters;

        /// <summary>
        /// Convert meters to feet.
        /// </summary>
        public static double FromMeters(double meters) => meters * MetersToFeet;

        /// <summary>
        /// Convert radians to degrees.
        /// </summary>
        public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

        /// <summary>
        /// Convert degrees to radians.
        /// </summary>
        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        #endregion

        #region Point Operations

        /// <summary>
        /// Create a point from millimeter coordinates.
        /// </summary>
        public static XYZ PointFromMm(double xMm, double yMm, double zMm = 0)
        {
            return new XYZ(ToFeet(xMm), ToFeet(yMm), ToFeet(zMm));
        }

        /// <summary>
        /// Get midpoint between two points.
        /// </summary>
        public static XYZ Midpoint(XYZ p1, XYZ p2)
        {
            if (p1 == null || p2 == null) return null;
            return (p1 + p2) / 2.0;
        }

        /// <summary>
        /// Get distance between two points.
        /// </summary>
        public static double Distance(XYZ p1, XYZ p2)
        {
            if (p1 == null || p2 == null) return 0;
            return p1.DistanceTo(p2);
        }

        /// <summary>
        /// Get horizontal (XY) distance between two points.
        /// </summary>
        public static double DistanceXY(XYZ p1, XYZ p2)
        {
            if (p1 == null || p2 == null) return 0;
            var dx = p2.X - p1.X;
            var dy = p2.Y - p1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Check if two points are approximately equal.
        /// </summary>
        public static bool IsAlmostEqual(XYZ p1, XYZ p2, double tolerance = Tolerance)
        {
            if (p1 == null || p2 == null) return false;
            return p1.DistanceTo(p2) < tolerance;
        }

        /// <summary>
        /// Project a point onto a plane (XY plane at specified Z).
        /// </summary>
        public static XYZ ProjectToXY(XYZ point, double z = 0)
        {
            if (point == null) return null;
            return new XYZ(point.X, point.Y, z);
        }

        #endregion

        #region Line Operations

        /// <summary>
        /// Get the length of a curve in feet.
        /// </summary>
        public static double GetLength(Curve curve)
        {
            return curve?.Length ?? 0;
        }

        /// <summary>
        /// Get the length of a curve in millimeters.
        /// </summary>
        public static double GetLengthMm(Curve curve)
        {
            return ToMm(curve?.Length ?? 0);
        }

        /// <summary>
        /// Get the midpoint of a curve.
        /// </summary>
        public static XYZ GetMidpoint(Curve curve)
        {
            if (curve == null) return null;
            return curve.Evaluate(0.5, true);
        }

        /// <summary>
        /// Get the direction vector of a line.
        /// </summary>
        public static XYZ GetDirection(Line line)
        {
            if (line == null) return null;
            return (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
        }

        /// <summary>
        /// Check if two lines are parallel.
        /// </summary>
        public static bool AreParallel(Line line1, Line line2, double tolerance = Tolerance)
        {
            if (line1 == null || line2 == null) return false;

            var dir1 = GetDirection(line1);
            var dir2 = GetDirection(line2);

            var cross = dir1.CrossProduct(dir2);
            return cross.GetLength() < tolerance;
        }

        /// <summary>
        /// Check if two lines are perpendicular.
        /// </summary>
        public static bool ArePerpendicular(Line line1, Line line2, double tolerance = Tolerance)
        {
            if (line1 == null || line2 == null) return false;

            var dir1 = GetDirection(line1);
            var dir2 = GetDirection(line2);

            return Math.Abs(dir1.DotProduct(dir2)) < tolerance;
        }

        /// <summary>
        /// Create a line from two points.
        /// </summary>
        public static Line CreateLine(XYZ start, XYZ end)
        {
            if (start == null || end == null) return null;
            if (IsAlmostEqual(start, end)) return null;

            return Line.CreateBound(start, end);
        }

        /// <summary>
        /// Offset a line by a distance (perpendicular in XY plane).
        /// </summary>
        public static Line OffsetLine(Line line, double offset)
        {
            if (line == null || Math.Abs(offset) < Tolerance) return line;

            var dir = GetDirection(line);
            var perpendicular = new XYZ(-dir.Y, dir.X, 0).Normalize();
            var offsetVector = perpendicular * offset;

            var newStart = line.GetEndPoint(0) + offsetVector;
            var newEnd = line.GetEndPoint(1) + offsetVector;

            return Line.CreateBound(newStart, newEnd);
        }

        #endregion

        #region BoundingBox Operations

        /// <summary>
        /// Get the bounding box of an element.
        /// </summary>
        public static BoundingBoxXYZ GetBoundingBox(Element element, View view = null)
        {
            return element?.get_BoundingBox(view);
        }

        /// <summary>
        /// Get the center point of a bounding box.
        /// </summary>
        public static XYZ GetCenter(BoundingBoxXYZ bbox)
        {
            if (bbox == null) return null;
            return (bbox.Min + bbox.Max) / 2.0;
        }

        /// <summary>
        /// Get the dimensions of a bounding box (width, depth, height) in feet.
        /// </summary>
        public static XYZ GetDimensions(BoundingBoxXYZ bbox)
        {
            if (bbox == null) return null;

            return new XYZ(
                Math.Abs(bbox.Max.X - bbox.Min.X),
                Math.Abs(bbox.Max.Y - bbox.Min.Y),
                Math.Abs(bbox.Max.Z - bbox.Min.Z));
        }

        /// <summary>
        /// Check if two bounding boxes intersect.
        /// </summary>
        public static bool Intersects(BoundingBoxXYZ bbox1, BoundingBoxXYZ bbox2)
        {
            if (bbox1 == null || bbox2 == null) return false;

            return bbox1.Min.X <= bbox2.Max.X && bbox1.Max.X >= bbox2.Min.X &&
                   bbox1.Min.Y <= bbox2.Max.Y && bbox1.Max.Y >= bbox2.Min.Y &&
                   bbox1.Min.Z <= bbox2.Max.Z && bbox1.Max.Z >= bbox2.Min.Z;
        }

        /// <summary>
        /// Expand a bounding box by a specified amount.
        /// </summary>
        public static BoundingBoxXYZ Expand(BoundingBoxXYZ bbox, double amount)
        {
            if (bbox == null) return null;

            var expanded = new BoundingBoxXYZ
            {
                Min = new XYZ(bbox.Min.X - amount, bbox.Min.Y - amount, bbox.Min.Z - amount),
                Max = new XYZ(bbox.Max.X + amount, bbox.Max.Y + amount, bbox.Max.Z + amount)
            };

            return expanded;
        }

        #endregion

        #region Element Geometry

        /// <summary>
        /// Get all solid geometry from an element.
        /// </summary>
        public static IList<Solid> GetSolids(Element element, Options options = null)
        {
            if (element == null) return new List<Solid>();

            options = options ?? new Options
            {
                ComputeReferences = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            var geometry = element.get_Geometry(options);
            if (geometry == null) return new List<Solid>();

            return GetSolidsFromGeometry(geometry);
        }

        /// <summary>
        /// Recursively extract solids from geometry element.
        /// </summary>
        private static IList<Solid> GetSolidsFromGeometry(GeometryElement geometry)
        {
            var solids = new List<Solid>();

            foreach (var geomObj in geometry)
            {
                if (geomObj is Solid solid && solid.Volume > 0)
                {
                    solids.Add(solid);
                }
                else if (geomObj is GeometryInstance instance)
                {
                    var instanceGeom = instance.GetInstanceGeometry();
                    if (instanceGeom != null)
                    {
                        solids.AddRange(GetSolidsFromGeometry(instanceGeom));
                    }
                }
            }

            return solids;
        }

        /// <summary>
        /// Get all faces from an element's geometry.
        /// </summary>
        public static IList<Face> GetFaces(Element element, Options options = null)
        {
            var solids = GetSolids(element, options);
            var faces = new List<Face>();

            foreach (var solid in solids)
            {
                foreach (Face face in solid.Faces)
                {
                    faces.Add(face);
                }
            }

            return faces;
        }

        /// <summary>
        /// Get all edges from an element's geometry.
        /// </summary>
        public static IList<Edge> GetEdges(Element element, Options options = null)
        {
            var solids = GetSolids(element, options);
            var edges = new List<Edge>();

            foreach (var solid in solids)
            {
                foreach (Edge edge in solid.Edges)
                {
                    edges.Add(edge);
                }
            }

            return edges;
        }

        /// <summary>
        /// Get the total volume of an element in cubic feet.
        /// </summary>
        public static double GetVolume(Element element)
        {
            var solids = GetSolids(element);
            return solids.Sum(s => s.Volume);
        }

        /// <summary>
        /// Get the total surface area of an element in square feet.
        /// </summary>
        public static double GetSurfaceArea(Element element)
        {
            var solids = GetSolids(element);
            return solids.Sum(s => s.SurfaceArea);
        }

        #endregion

        #region Transform Operations

        /// <summary>
        /// Create a translation transform.
        /// </summary>
        public static Transform CreateTranslation(XYZ vector)
        {
            return Transform.CreateTranslation(vector);
        }

        /// <summary>
        /// Create a rotation transform around Z axis.
        /// </summary>
        public static Transform CreateRotationZ(XYZ origin, double angleRadians)
        {
            var axis = Line.CreateUnbound(origin, XYZ.BasisZ);
            return Transform.CreateRotationAtPoint(XYZ.BasisZ, angleRadians, origin);
        }

        /// <summary>
        /// Get the location point of an element.
        /// </summary>
        public static XYZ GetLocationPoint(Element element)
        {
            if (element?.Location is LocationPoint locPt)
            {
                return locPt.Point;
            }
            return null;
        }

        /// <summary>
        /// Get the location curve of an element.
        /// </summary>
        public static Curve GetLocationCurve(Element element)
        {
            if (element?.Location is LocationCurve locCrv)
            {
                return locCrv.Curve;
            }
            return null;
        }

        /// <summary>
        /// Move an element by a vector.
        /// </summary>
        public static bool MoveElement(Document doc, Element element, XYZ vector)
        {
            if (doc == null || element == null || vector == null) return false;

            try
            {
                ElementTransformUtils.MoveElement(doc, element.Id, vector);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Rotate an element around an axis.
        /// </summary>
        public static bool RotateElement(Document doc, Element element, Line axis, double angleRadians)
        {
            if (doc == null || element == null || axis == null) return false;

            try
            {
                ElementTransformUtils.RotateElement(doc, element.Id, axis, angleRadians);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Curve Loop Operations

        /// <summary>
        /// Create a rectangular curve loop.
        /// </summary>
        public static CurveLoop CreateRectangle(XYZ center, double width, double depth, double z = 0)
        {
            var halfW = width / 2.0;
            var halfD = depth / 2.0;

            var p1 = new XYZ(center.X - halfW, center.Y - halfD, z);
            var p2 = new XYZ(center.X + halfW, center.Y - halfD, z);
            var p3 = new XYZ(center.X + halfW, center.Y + halfD, z);
            var p4 = new XYZ(center.X - halfW, center.Y + halfD, z);

            var curves = new List<Curve>
            {
                Line.CreateBound(p1, p2),
                Line.CreateBound(p2, p3),
                Line.CreateBound(p3, p4),
                Line.CreateBound(p4, p1)
            };

            return CurveLoop.Create(curves);
        }

        /// <summary>
        /// Get the area enclosed by a curve loop.
        /// </summary>
        public static double GetArea(CurveLoop loop)
        {
            if (loop == null) return 0;

            // Use shoelace formula for 2D area
            double area = 0;
            var curves = loop.ToList();

            foreach (var curve in curves)
            {
                var p1 = curve.GetEndPoint(0);
                var p2 = curve.GetEndPoint(1);
                area += (p1.X * p2.Y - p2.X * p1.Y);
            }

            return Math.Abs(area) / 2.0;
        }

        /// <summary>
        /// Get the perimeter of a curve loop.
        /// </summary>
        public static double GetPerimeter(CurveLoop loop)
        {
            if (loop == null) return 0;
            return loop.Sum(c => c.Length);
        }

        #endregion
    }
}
