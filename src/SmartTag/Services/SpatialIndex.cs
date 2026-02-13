using System;
using System.Collections.Generic;
using System.Linq;
using SmartTag.Models;

namespace SmartTag.Services
{
    /// <summary>
    /// Grid-based spatial index for efficient collision detection.
    /// Divides the view space into cells for O(1) average lookup.
    /// </summary>
    public class SpatialIndex
    {
        private readonly double _cellSize;
        private readonly Dictionary<(int, int), List<IndexedItem>> _grid;
        private readonly List<IndexedItem> _allItems;

        public SpatialIndex(double cellSize = 5.0) // 5 feet cells
        {
            _cellSize = cellSize;
            _grid = new Dictionary<(int, int), List<IndexedItem>>();
            _allItems = new List<IndexedItem>();
        }

        /// <summary>
        /// Add an item to the index.
        /// </summary>
        public void Add(long id, BoundingBox2D bounds, object data = null)
        {
            var item = new IndexedItem { Id = id, Bounds = bounds, Data = data };
            _allItems.Add(item);

            // Add to all cells that the bounds overlap
            var minCell = GetCell(bounds.MinX, bounds.MinY);
            var maxCell = GetCell(bounds.MaxX, bounds.MaxY);

            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                for (int y = minCell.y; y <= maxCell.y; y++)
                {
                    var key = (x, y);
                    if (!_grid.TryGetValue(key, out var cell))
                    {
                        cell = new List<IndexedItem>();
                        _grid[key] = cell;
                    }
                    cell.Add(item);
                }
            }
        }

        /// <summary>
        /// Find all items that intersect with the given bounds.
        /// </summary>
        public List<IndexedItem> Query(BoundingBox2D bounds)
        {
            var result = new HashSet<IndexedItem>();

            var minCell = GetCell(bounds.MinX, bounds.MinY);
            var maxCell = GetCell(bounds.MaxX, bounds.MaxY);

            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                for (int y = minCell.y; y <= maxCell.y; y++)
                {
                    if (_grid.TryGetValue((x, y), out var cell))
                    {
                        foreach (var item in cell)
                        {
                            if (item.Bounds.Intersects(bounds))
                            {
                                result.Add(item);
                            }
                        }
                    }
                }
            }

            return result.ToList();
        }

        /// <summary>
        /// Find all items within a radius of a point.
        /// </summary>
        public List<IndexedItem> QueryRadius(Point2D center, double radius)
        {
            var searchBounds = new BoundingBox2D(
                center.X - radius,
                center.Y - radius,
                center.X + radius,
                center.Y + radius);

            return Query(searchBounds)
                .Where(item => item.Bounds.Center.DistanceTo(center) <= radius)
                .ToList();
        }

        /// <summary>
        /// Check if a bounding box collides with any existing items.
        /// </summary>
        public bool HasCollision(BoundingBox2D bounds, long? excludeId = null)
        {
            var candidates = Query(bounds);
            foreach (var candidate in candidates)
            {
                if (excludeId.HasValue && candidate.Id == excludeId.Value)
                    continue;
                if (candidate.Bounds.Intersects(bounds))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get all colliding items for a bounding box.
        /// </summary>
        public List<IndexedItem> GetCollisions(BoundingBox2D bounds, long? excludeId = null)
        {
            return Query(bounds)
                .Where(item => (!excludeId.HasValue || item.Id != excludeId.Value) && 
                               item.Bounds.Intersects(bounds))
                .ToList();
        }

        /// <summary>
        /// Remove an item from the index.
        /// </summary>
        public void Remove(long id)
        {
            var item = _allItems.FirstOrDefault(i => i.Id == id);
            if (item == null) return;

            _allItems.Remove(item);

            var minCell = GetCell(item.Bounds.MinX, item.Bounds.MinY);
            var maxCell = GetCell(item.Bounds.MaxX, item.Bounds.MaxY);

            for (int x = minCell.x; x <= maxCell.x; x++)
            {
                for (int y = minCell.y; y <= maxCell.y; y++)
                {
                    if (_grid.TryGetValue((x, y), out var cell))
                    {
                        cell.RemoveAll(i => i.Id == id);
                    }
                }
            }
        }

        /// <summary>
        /// Clear all items from the index.
        /// </summary>
        public void Clear()
        {
            _grid.Clear();
            _allItems.Clear();
        }

        /// <summary>
        /// Get all items in the index.
        /// </summary>
        public IReadOnlyList<IndexedItem> GetAll() => _allItems;

        private (int x, int y) GetCell(double x, double y)
        {
            return ((int)Math.Floor(x / _cellSize), (int)Math.Floor(y / _cellSize));
        }
    }

    /// <summary>
    /// Item stored in the spatial index.
    /// </summary>
    public class IndexedItem
    {
        public long Id { get; set; }
        public BoundingBox2D Bounds { get; set; }
        public object Data { get; set; }
    }
}
