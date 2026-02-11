# Collector Optimization Examples

## Example 1: Wall Collection with Parameter Filter

### Before (Slow)

```csharp
public IEnumerable<Wall> GetExteriorWalls(Document doc)
{
    var allElements = new FilteredElementCollector(doc)
        .ToElements();
    
    return allElements
        .OfType<Wall>()
        .Where(w => w.WallType.Function == WallFunction.Exterior);
}
```

**Problems**:
- `.ToElements()` materializes ALL elements in model
- LINQ `.OfType<Wall>()` iterates entire list
- Parameter check happens in memory, not at API level

### After (Fast)

```csharp
public IEnumerable<Wall> GetExteriorWalls(Document doc)
{
    var exteriorParam = new ElementParameterFilter(
        new FilterRule[] {
            ParameterFilterRuleFactory.CreateEqualsRule(
                new ElementId(BuiltInParameter.FUNCTION_PARAM),
                (int)WallFunction.Exterior)
        });

    return new FilteredElementCollector(doc)
        .OfClass(typeof(Wall))
        .WherePasses(exteriorParam)
        .Cast<Wall>();
}
```

**Improvements**:
- `OfClass` filters at API level (quick filter)
- Parameter filter runs before enumeration
- No intermediate list allocation

---

## Example 2: Elements in View

### Before (Slow)

```csharp
public List<ElementId> GetVisibleElementIds(Document doc, View view)
{
    var collector = new FilteredElementCollector(doc, view.Id);
    var elements = collector.ToElements();
    
    var result = new List<ElementId>();
    foreach (var elem in elements)
    {
        if (elem.CanBeHidden(view))
        {
            result.Add(elem.Id);
        }
    }
    return result;
}
```

**Problems**:
- Stores full Element objects
- Manual iteration after materialization
- Memory pressure from Element references

### After (Fast)

```csharp
public List<ElementId> GetVisibleElementIds(Document doc, View view)
{
    return new FilteredElementCollector(doc, view.Id)
        .WhereElementIsNotElementType()
        .ToElementIds()
        .ToList();
}
```

**Improvements**:
- Uses view-scoped collector (already filtered)
- Returns `ElementId` instead of `Element`
- Single enumeration

---

## Example 3: Multi-Category Collection

### Before (Slow)

```csharp
public List<Element> GetStructuralElements(Document doc)
{
    var result = new List<Element>();
    
    var walls = new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_Walls)
        .ToElements();
    result.AddRange(walls);
    
    var floors = new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_Floors)
        .ToElements();
    result.AddRange(floors);
    
    var columns = new FilteredElementCollector(doc)
        .OfCategory(BuiltInCategory.OST_StructuralColumns)
        .ToElements();
    result.AddRange(columns);
    
    return result;
}
```

**Problems**:
- Three separate collectors = three model scans
- Multiple list allocations

### After (Fast)

```csharp
public List<Element> GetStructuralElements(Document doc)
{
    var categories = new List<BuiltInCategory>
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_StructuralColumns
    };
    
    var categoryFilter = new ElementMulticategoryFilter(categories);
    
    return new FilteredElementCollector(doc)
        .WherePasses(categoryFilter)
        .WhereElementIsNotElementType()
        .ToElements()
        .ToList();
}
```

**Improvements**:
- Single collector with multi-category filter
- One model scan instead of three
- Single list allocation

---

## Example 4: Python/pyRevit Optimization

### Before (Slow)

```python
from pyrevit import revit, DB

doc = revit.doc

# Collect all rooms with area > 100 sqft
rooms = []
all_elements = DB.FilteredElementCollector(doc).ToElements()

for elem in all_elements:
    if isinstance(elem, DB.SpatialElement):
        if hasattr(elem, 'Area') and elem.Area > 100:
            rooms.append(elem)

print("Found {} rooms".format(len(rooms)))
```

**Problems**:
- Collects ALL elements in model
- Python isinstance check is slow
- No API-level filtering

### After (Fast)

```python
from pyrevit import revit, DB

doc = revit.doc

# Use API filter for rooms
rooms = DB.FilteredElementCollector(doc)\
    .OfCategory(DB.BuiltInCategory.OST_Rooms)\
    .WhereElementIsNotElementType()\
    .ToElements()

# Filter by area (still need Python for param comparison)
large_rooms = [r for r in rooms if r.Area > 100]

print("Found {} rooms".format(len(large_rooms)))
```

**Improvements**:
- Category filter at API level
- Much smaller set for Python iteration
- Clear separation of API vs Python filtering

---

## Example 5: Avoiding Re-enumeration

### Before (Slow)

```csharp
public void ProcessWalls(Document doc)
{
    var collector = new FilteredElementCollector(doc)
        .OfClass(typeof(Wall));
    
    // Problem: each call re-enumerates
    if (collector.Any())
    {
        var count = collector.Count();
        var first = collector.First();
        
        foreach (var wall in collector)
        {
            // process...
        }
    }
}
```

**Problems**:
- `.Any()` enumerates once
- `.Count()` enumerates again
- `.First()` enumerates again
- `foreach` enumerates again
- Total: 4 full enumerations!

### After (Fast)

```csharp
public void ProcessWalls(Document doc)
{
    var walls = new FilteredElementCollector(doc)
        .OfClass(typeof(Wall))
        .Cast<Wall>()
        .ToList();  // Single enumeration
    
    if (walls.Count > 0)
    {
        var first = walls[0];
        
        foreach (var wall in walls)
        {
            // process...
        }
    }
}
```

**Improvements**:
- Single enumeration into list
- All subsequent operations on cached list
- Predictable memory usage

---

## Example 6: Linked Document Elements

### Before (Problematic)

```csharp
public List<Element> GetAllWallsIncludingLinks(Document doc)
{
    var result = new List<Element>();
    
    // Host doc walls
    result.AddRange(new FilteredElementCollector(doc)
        .OfClass(typeof(Wall))
        .ToElements());
    
    // This doesn't work correctly!
    var links = new FilteredElementCollector(doc)
        .OfClass(typeof(RevitLinkInstance))
        .ToElements();
        
    foreach (RevitLinkInstance link in links)
    {
        var linkDoc = link.GetLinkDocument();
        if (linkDoc != null)
        {
            // Problem: Elements from link doc have different context
            result.AddRange(new FilteredElementCollector(linkDoc)
                .OfClass(typeof(Wall))
                .ToElements());
        }
    }
    
    return result;
}
```

**Problems**:
- Mixed Element contexts (host vs link)
- No transform consideration
- Memory heavy with Element storage

### After (Correct)

```csharp
public List<(ElementId, Document)> GetAllWallsIncludingLinks(Document doc)
{
    var result = new List<(ElementId, Document)>();
    
    // Host doc walls
    var hostWalls = new FilteredElementCollector(doc)
        .OfClass(typeof(Wall))
        .ToElementIds();
    
    foreach (var id in hostWalls)
        result.Add((id, doc));
    
    // Link doc walls with proper context
    var links = new FilteredElementCollector(doc)
        .OfClass(typeof(RevitLinkInstance))
        .Cast<RevitLinkInstance>();
        
    foreach (var link in links)
    {
        var linkDoc = link.GetLinkDocument();
        if (linkDoc != null)
        {
            var linkWalls = new FilteredElementCollector(linkDoc)
                .OfClass(typeof(Wall))
                .ToElementIds();
            
            foreach (var id in linkWalls)
                result.Add((id, linkDoc));
        }
    }
    
    return result;
}
```

**Improvements**:
- Stores ElementId + Document context tuple
- Lightweight storage
- Clear ownership tracking
