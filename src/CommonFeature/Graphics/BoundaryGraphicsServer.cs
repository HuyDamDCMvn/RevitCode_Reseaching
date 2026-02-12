using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using CommonFeature.Models;

namespace CommonFeature.Graphics
{
    /// <summary>
    /// DirectContext3D server for rendering boundary preview graphics in Revit viewport.
    /// 
    /// Overview:
    /// This server draws temporary 3D graphics (bounding boxes and spheres) without
    /// modifying the Revit document. Graphics are automatically removed when the server
    /// is unregistered.
    /// 
    /// Compatibility:
    /// - Revit 2023-2026 (DirectContext3D API is stable across these versions)
    /// - Works in 3D, FloorPlan, CeilingPlan, Section, and Elevation views
    /// 
    /// Thread Safety:
    /// - All data access is protected by _lock
    /// - UpdateData/ClearData can be called from any thread
    /// - RenderScene is called by Revit on its thread
    /// 
    /// Memory Management:
    /// - VertexBuffer/IndexBuffer are disposed when data changes or server unregisters
    /// - Geometry is rebuilt lazily (only when _needsUpdate is true)
    /// </summary>
    public class BoundaryGraphicsServer : IDirectContext3DServer
    {
        #region Fields

        private readonly Guid _serverId;
        private Document _document;
        private List<ElementBoundaryData> _boundaryDataList = new();
        private BoundaryDisplaySettings _settings;
        private readonly object _lock = new();
        
        // Cached geometry buffers - rebuilt when data changes
        private List<RenderData> _renderDataList = new();
        private bool _needsUpdate = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Create a new graphics server for the given document.
        /// Call Register() after creation to activate the server.
        /// </summary>
        /// <param name="doc">Revit document this server will render in.</param>
        public BoundaryGraphicsServer(Document doc)
        {
            _document = doc;
            _serverId = Guid.NewGuid();
        }

        #endregion

        #region IDirectContext3DServer Implementation

        public Guid GetServerId() => _serverId;

        public string GetVendorId() => "HD-Tools";

        public string GetName() => "BoundaryPreviewServer";

        public string GetDescription() => "Displays boundary box and points for selected elements";

        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;

        public string GetApplicationId() => "CommonFeature.BoundaryPreview";

        public string GetSourceId() => "";

        public bool UsesHandles() => false;

        public bool CanExecute(View dBView)
        {
            // Only render in 3D views and the active view
            if (dBView == null) return false;
            if (!(dBView is View3D || dBView.ViewType == ViewType.ThreeD ||
                  dBView.ViewType == ViewType.FloorPlan || 
                  dBView.ViewType == ViewType.CeilingPlan ||
                  dBView.ViewType == ViewType.Section ||
                  dBView.ViewType == ViewType.Elevation))
                return false;
            
            lock (_lock)
            {
                return _boundaryDataList.Count > 0 && _settings != null;
            }
        }

        public Outline GetBoundingBox(View dBView)
        {
            lock (_lock)
            {
                if (_boundaryDataList.Count == 0) return null;

                // Calculate combined bounding box
                XYZ minPt = null;
                XYZ maxPt = null;

                foreach (var data in _boundaryDataList)
                {
                    if (data.BoundingBox == null) continue;
                    
                    var bbMin = data.BoundingBox.Min;
                    var bbMax = data.BoundingBox.Max;

                    if (minPt == null)
                    {
                        minPt = bbMin;
                        maxPt = bbMax;
                    }
                    else
                    {
                        minPt = new XYZ(
                            Math.Min(minPt.X, bbMin.X),
                            Math.Min(minPt.Y, bbMin.Y),
                            Math.Min(minPt.Z, bbMin.Z));
                        maxPt = new XYZ(
                            Math.Max(maxPt.X, bbMax.X),
                            Math.Max(maxPt.Y, bbMax.Y),
                            Math.Max(maxPt.Z, bbMax.Z));
                    }
                }

                if (minPt == null || maxPt == null) return null;
                
                // Add padding for sphere display
                double padding = (_settings?.SphereDiameterMm ?? 100) / 304.8 * 2; // mm to feet
                minPt = new XYZ(minPt.X - padding, minPt.Y - padding, minPt.Z - padding);
                maxPt = new XYZ(maxPt.X + padding, maxPt.Y + padding, maxPt.Z + padding);
                
                return new Outline(minPt, maxPt);
            }
        }

        public void RenderScene(View dBView, DisplayStyle displayStyle)
        {
            lock (_lock)
            {
                if (_boundaryDataList.Count == 0 || _settings == null) return;

                // Rebuild geometry if needed
                if (_needsUpdate)
                {
                    RebuildGeometry();
                    _needsUpdate = false;
                }

                // Render all cached geometry
                foreach (var renderData in _renderDataList)
                {
                    if (renderData.VertexBuffer == null) continue;
                    
                    DrawContext.FlushBuffer(
                        renderData.VertexBuffer,
                        renderData.VertexCount,
                        renderData.IndexBuffer,
                        renderData.IndexCount,
                        renderData.VertexFormat,
                        renderData.Effect,
                        renderData.PrimitiveType,
                        0,
                        renderData.PrimitiveCount);
                }
            }
        }

        public bool UseInTransparentPass(View dBView) => true;

        #endregion

        #region Public Methods

        /// <summary>
        /// Update boundary data and settings
        /// </summary>
        public void UpdateData(List<ElementBoundaryData> dataList, BoundaryDisplaySettings settings)
        {
            lock (_lock)
            {
                _boundaryDataList = dataList ?? new List<ElementBoundaryData>();
                _settings = settings;
                _needsUpdate = true;
                
                // Clear old buffers
                ClearRenderData();
            }
        }

        /// <summary>
        /// Clear all data
        /// </summary>
        public void ClearData()
        {
            lock (_lock)
            {
                _boundaryDataList.Clear();
                _settings = null;
                ClearRenderData();
            }
        }

        /// <summary>
        /// Register this server with Revit
        /// </summary>
        public void Register()
        {
            var directContext3DService = ExternalServiceRegistry.GetService(
                ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
            
            if (directContext3DService != null)
            {
                directContext3DService.AddServer(this);
                
                // Activate this server
                var serverIds = directContext3DService.GetActiveServerIds();
                var newServerIds = new List<Guid>(serverIds) { _serverId };
                directContext3DService.SetActiveServers(newServerIds);
            }
        }

        /// <summary>
        /// Unregister this server
        /// </summary>
        public void Unregister()
        {
            try
            {
                var directContext3DService = ExternalServiceRegistry.GetService(
                    ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
                
                if (directContext3DService != null)
                {
                    // Remove from active servers
                    var serverIds = directContext3DService.GetActiveServerIds().ToList();
                    if (serverIds.Contains(_serverId))
                    {
                        serverIds.Remove(_serverId);
                        directContext3DService.SetActiveServers(serverIds);
                    }
                    
                    directContext3DService.RemoveServer(_serverId);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        #endregion

        #region Private Methods - Geometry Building

        private void ClearRenderData()
        {
            foreach (var rd in _renderDataList)
            {
                rd.VertexBuffer?.Dispose();
                rd.IndexBuffer?.Dispose();
            }
            _renderDataList.Clear();
        }

        private void RebuildGeometry()
        {
            ClearRenderData();
            
            if (_boundaryDataList.Count == 0 || _settings == null) return;

            try
            {
                foreach (var data in _boundaryDataList)
                {
                    if (data == null) continue;
                    
                    // Build bounding box lines
                    if (_settings.ShowBoundingBox && data.BoundingBox != null)
                    {
                        try
                        {
                            var boxRenderData = BuildBoundingBoxGeometry(data, _settings);
                            if (boxRenderData != null)
                            {
                                _renderDataList.Add(boxRenderData);
                            }
                        }
                        catch
                        {
                            // Skip this bounding box if it fails
                        }
                    }

                    // Build points (spheres)
                    double sphereRadius = (_settings.SphereDiameterMm / 2.0) / 304.8; // mm to feet
                    
                    // Clamp sphere radius to reasonable bounds
                    sphereRadius = Math.Max(0.01, Math.Min(sphereRadius, 10.0)); // 0.01 to 10 feet
                    
                    if (_settings.ShowMinPoint && data.MinPoint != null)
                    {
                        try
                        {
                            var minPointData = BuildSphereGeometry(data.MinPoint, sphereRadius, _settings.MinPointColor);
                            if (minPointData != null)
                            {
                                _renderDataList.Add(minPointData);
                            }
                        }
                        catch { /* Skip */ }
                    }
                    
                    if (_settings.ShowMaxPoint && data.MaxPoint != null)
                    {
                        try
                        {
                            var maxPointData = BuildSphereGeometry(data.MaxPoint, sphereRadius, _settings.MaxPointColor);
                            if (maxPointData != null)
                            {
                                _renderDataList.Add(maxPointData);
                            }
                        }
                        catch { /* Skip */ }
                    }
                    
                    if (_settings.ShowCentroid && data.Centroid != null)
                    {
                        try
                        {
                            var centroidData = BuildSphereGeometry(data.Centroid, sphereRadius, _settings.CentroidColor);
                            if (centroidData != null)
                            {
                                _renderDataList.Add(centroidData);
                            }
                        }
                        catch { /* Skip */ }
                    }
                }
            }
            catch (Exception)
            {
                // If entire rebuild fails, clear everything to prevent partial render
                ClearRenderData();
            }
        }

        private RenderData BuildBoundingBoxGeometry(ElementBoundaryData data, BoundaryDisplaySettings settings)
        {
            var bbox = data.BoundingBox;
            if (bbox == null) return null;
            if (bbox.Min == null || bbox.Max == null) return null;

            // Get the 8 corners of the bounding box
            var corners = GetBoundingBoxCorners(bbox);
            
            // Apply rotation transform if using rotated mode
            // Note: This rotates the world-aligned bbox corners around the centroid
            if (settings.UseRotatedBoundingBox && data.RotationTransform != null)
            {
                try
                {
                    // Calculate centroid for rotation pivot
                    var centroid = new XYZ(
                        (bbox.Min.X + bbox.Max.X) / 2,
                        (bbox.Min.Y + bbox.Max.Y) / 2,
                        (bbox.Min.Z + bbox.Max.Z) / 2);
                    
                    // Get rotation from transform (extract rotation component only)
                    var rotationOnly = GetRotationOnlyTransform(data.RotationTransform, centroid);
                    
                    if (rotationOnly != null)
                    {
                        corners = corners.Select(c => rotationOnly.OfPoint(c)).ToArray();
                    }
                }
                catch
                {
                    // If rotation fails, use original corners
                }
            }

            // 12 edges of a box
            var edges = new[]
            {
                // Bottom face
                (0, 1), (1, 2), (2, 3), (3, 0),
                // Top face
                (4, 5), (5, 6), (6, 7), (7, 4),
                // Vertical edges
                (0, 4), (1, 5), (2, 6), (3, 7)
            };

            // Build line vertices - use default color if null
            var color = settings.BoundingBoxColor ?? new Color(33, 150, 243); // Blue default
            var vertices = new List<VertexPositionColored>();
            var colorRef = new ColorWithTransparency(color.Red, color.Green, color.Blue, 0);

            foreach (var (i1, i2) in edges)
            {
                vertices.Add(new VertexPositionColored(corners[i1], colorRef));
                vertices.Add(new VertexPositionColored(corners[i2], colorRef));
            }

            // Create vertex buffer
            var vertexFormat = new VertexFormat(VertexFormatBits.PositionColored);
            int vertexCount = vertices.Count;
            int vertexBufferSize = VertexPositionColored.GetSizeInFloats() * vertexCount;
            
            var vertexBuffer = new VertexBuffer(vertexBufferSize);
            vertexBuffer.Map(vertexBufferSize);
            
            using (var stream = vertexBuffer.GetVertexStreamPositionColored())
            {
                foreach (var v in vertices)
                {
                    stream.AddVertex(v);
                }
            }
            
            vertexBuffer.Unmap();

            // Create index buffer (for lines, indices are sequential pairs)
            int indexCount = vertexCount;
            var indexBuffer = new IndexBuffer(indexCount);
            indexBuffer.Map(indexCount);
            
            using (var stream = indexBuffer.GetIndexStreamLine())
            {
                for (int i = 0; i < vertexCount; i += 2)
                {
                    stream.AddLine(new IndexLine(i, i + 1));
                }
            }
            
            indexBuffer.Unmap();

            // Create effect
            var effectInstance = new EffectInstance(VertexFormatBits.PositionColored);

            return new RenderData
            {
                VertexBuffer = vertexBuffer,
                VertexCount = vertexCount,
                IndexBuffer = indexBuffer,
                IndexCount = indexCount,
                VertexFormat = vertexFormat,
                Effect = effectInstance,
                PrimitiveType = PrimitiveType.LineList,
                PrimitiveCount = vertexCount / 2
            };
        }

        private XYZ[] GetBoundingBoxCorners(BoundingBoxXYZ bbox)
        {
            var min = bbox.Min;
            var max = bbox.Max;

            return new[]
            {
                new XYZ(min.X, min.Y, min.Z), // 0: bottom-front-left
                new XYZ(max.X, min.Y, min.Z), // 1: bottom-front-right
                new XYZ(max.X, max.Y, min.Z), // 2: bottom-back-right
                new XYZ(min.X, max.Y, min.Z), // 3: bottom-back-left
                new XYZ(min.X, min.Y, max.Z), // 4: top-front-left
                new XYZ(max.X, min.Y, max.Z), // 5: top-front-right
                new XYZ(max.X, max.Y, max.Z), // 6: top-back-right
                new XYZ(min.X, max.Y, max.Z), // 7: top-back-left
            };
        }
        
        /// <summary>
        /// Extract rotation-only transform around a pivot point
        /// </summary>
        private Transform GetRotationOnlyTransform(Transform original, XYZ pivot)
        {
            if (original == null) return null;
            
            try
            {
                // Get rotation component (basis vectors)
                var basisX = original.BasisX;
                var basisY = original.BasisY;
                var basisZ = original.BasisZ;
                
                // Check if it's actually rotated (not just translated)
                if (basisX.IsAlmostEqualTo(XYZ.BasisX) && 
                    basisY.IsAlmostEqualTo(XYZ.BasisY) && 
                    basisZ.IsAlmostEqualTo(XYZ.BasisZ))
                {
                    return null; // No rotation
                }
                
                // Create rotation transform around pivot
                // 1. Translate to origin
                // 2. Apply rotation
                // 3. Translate back
                var toOrigin = Transform.CreateTranslation(-pivot);
                var rotation = Transform.Identity;
                rotation.BasisX = basisX;
                rotation.BasisY = basisY;
                rotation.BasisZ = basisZ;
                var toBack = Transform.CreateTranslation(pivot);
                
                // Combine: toBack * rotation * toOrigin
                return toBack.Multiply(rotation.Multiply(toOrigin));
            }
            catch
            {
                return null;
            }
        }

        private RenderData BuildSphereGeometry(XYZ center, double radius, Autodesk.Revit.DB.Color color)
        {
            // Guard: validate inputs
            if (center == null) return null;
            if (radius <= 0 || radius > 100) return null; // Max 100 feet radius for safety
            
            // Use default color if null
            var safeColor = color ?? new Color(255, 193, 7); // Yellow default
            
            // Build a UV sphere
            const int latSegments = 8;
            const int lonSegments = 12;
            
            var vertices = new List<VertexPositionColored>();
            var indices = new List<int>();
            
            var colorRef = new ColorWithTransparency(safeColor.Red, safeColor.Green, safeColor.Blue, 0);

            // Generate sphere vertices
            for (int lat = 0; lat <= latSegments; lat++)
            {
                double theta = lat * Math.PI / latSegments;
                double sinTheta = Math.Sin(theta);
                double cosTheta = Math.Cos(theta);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    double phi = lon * 2 * Math.PI / lonSegments;
                    double sinPhi = Math.Sin(phi);
                    double cosPhi = Math.Cos(phi);

                    double x = center.X + radius * sinTheta * cosPhi;
                    double y = center.Y + radius * sinTheta * sinPhi;
                    double z = center.Z + radius * cosTheta;

                    vertices.Add(new VertexPositionColored(new XYZ(x, y, z), colorRef));
                }
            }

            // Generate sphere indices (triangles)
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int first = lat * (lonSegments + 1) + lon;
                    int second = first + lonSegments + 1;

                    indices.Add(first);
                    indices.Add(second);
                    indices.Add(first + 1);

                    indices.Add(second);
                    indices.Add(second + 1);
                    indices.Add(first + 1);
                }
            }

            // Create vertex buffer
            var vertexFormat = new VertexFormat(VertexFormatBits.PositionColored);
            int vertexCount = vertices.Count;
            int vertexBufferSize = VertexPositionColored.GetSizeInFloats() * vertexCount;
            
            var vertexBuffer = new VertexBuffer(vertexBufferSize);
            vertexBuffer.Map(vertexBufferSize);
            
            using (var stream = vertexBuffer.GetVertexStreamPositionColored())
            {
                foreach (var v in vertices)
                {
                    stream.AddVertex(v);
                }
            }
            
            vertexBuffer.Unmap();

            // Create index buffer (triangles)
            int indexCount = indices.Count;
            var indexBuffer = new IndexBuffer(indexCount);
            indexBuffer.Map(indexCount);
            
            using (var stream = indexBuffer.GetIndexStreamTriangle())
            {
                for (int i = 0; i < indexCount; i += 3)
                {
                    stream.AddTriangle(new IndexTriangle(indices[i], indices[i + 1], indices[i + 2]));
                }
            }
            
            indexBuffer.Unmap();

            // Create effect
            var effectInstance = new EffectInstance(VertexFormatBits.PositionColored);

            return new RenderData
            {
                VertexBuffer = vertexBuffer,
                VertexCount = vertexCount,
                IndexBuffer = indexBuffer,
                IndexCount = indexCount,
                VertexFormat = vertexFormat,
                Effect = effectInstance,
                PrimitiveType = PrimitiveType.TriangleList,
                PrimitiveCount = indexCount / 3
            };
        }

        #endregion

        #region Nested Types

        private class RenderData
        {
            public VertexBuffer VertexBuffer { get; set; }
            public int VertexCount { get; set; }
            public IndexBuffer IndexBuffer { get; set; }
            public int IndexCount { get; set; }
            public VertexFormat VertexFormat { get; set; }
            public EffectInstance Effect { get; set; }
            public PrimitiveType PrimitiveType { get; set; }
            public int PrimitiveCount { get; set; }
        }

        #endregion
    }
}
