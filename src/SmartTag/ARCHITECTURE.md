# SmartTag Architecture

## Overview

SmartTag is an AI-powered auto-annotation tool for Revit, inspired by BIMLOGIQ's Smart Annotation product.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                            pyRevit Extension                             │
│  HD.extension/HD.tab/Labeling.panel/SmartTag.pushbutton/script.py      │
│                                   │                                      │
│                            launch_dll()                                  │
└───────────────────────────────────┼──────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                            SmartTag.dll                                  │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌──────────────┐     ┌──────────────────┐     ┌──────────────────┐    │
│  │    Entry     │────▶│  SmartTagWindow  │────▶│ SmartTagViewModel│    │
│  │  (Static)    │     │     (XAML)       │     │    (MVVM)        │    │
│  └──────────────┘     └──────────────────┘     └────────┬─────────┘    │
│                                                          │              │
│                                                          ▼              │
│                                               ┌──────────────────┐      │
│                                               │  SmartTagHandler │      │
│                                               │ (ExternalEvent)  │      │
│                                               └────────┬─────────┘      │
│                                                        │                │
│         ┌─────────────────────────────────────────────┼────────────┐   │
│         │                      SERVICES                │            │   │
│         │                                              ▼            │   │
│  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐  │   │
│  │ ElementCollector │  │TagPlacementService│  │TagCreationService│  │   │
│  │  - Get elements  │  │  - Calculate      │  │  - Create tags   │  │   │
│  │  - Get tags      │  │    placements     │  │  - Set leaders   │  │   │
│  │  - Get stats     │  │  - Resolve        │  │                  │  │   │
│  │                  │  │    collisions     │  │                  │  │   │
│  └──────────────────┘  └────────┬─────────┘  └──────────────────┘  │   │
│                                 │                                   │   │
│                                 ▼                                   │   │
│                        ┌──────────────────┐                         │   │
│                        │   SpatialIndex   │                         │   │
│                        │  (Grid-based)    │                         │   │
│                        └──────────────────┘                         │   │
│         └──────────────────────────────────────────────────────────┘   │
│                                                                          │
│         ┌──────────────────────────────────────────────────────────┐   │
│         │                      MODELS                               │   │
│         │  - TaggableElement   - TagPlacement   - TagSettings      │   │
│         │  - BoundingBox2D     - Point2D        - TagResult        │   │
│         │  - CategoryTagConfig                                      │   │
│         └──────────────────────────────────────────────────────────┘   │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

## Data Flow

### 1. User Opens Tool

```
User clicks button → script.py → Entry.ShowTool() → SmartTagWindow.Show()
```

### 2. Load Categories

```
SmartTagViewModel                  SmartTagHandler                    Revit
      │                                  │                              │
      │──RefreshCategories()────────────▶│                              │
      │                                  │──GetCategoryStats()─────────▶│
      │                                  │◀─────────────────────────────│
      │◀──OnCategoryStatsLoaded()───────│                              │
      │                                  │                              │
```

### 3. Execute Auto-Tag (Quick Mode)

```
SmartTagViewModel                  SmartTagHandler                    Revit
      │                                  │                              │
      │──ExecuteAutoTag()───────────────▶│                              │
      │                                  │                              │
      │                                  │──1. Collect Elements────────▶│
      │                                  │◀─────────────────────────────│
      │                                  │                              │
      │                                  │──2. Get Existing Tags───────▶│
      │                                  │◀─────────────────────────────│
      │                                  │                              │
      │                                  │──3. Calculate Placements     │
      │                                  │   (TagPlacementService)      │
      │                                  │                              │
      │                                  │──4. Resolve Collisions       │
      │                                  │   (SpatialIndex)             │
      │                                  │                              │
      │                                  │──5. Create Tags─────────────▶│
      │                                  │   (Transaction)              │
      │                                  │◀─────────────────────────────│
      │                                  │                              │
      │◀──OnAutoTagCompleted()──────────│                              │
      │                                  │                              │
```

## Algorithm: Quick Mode Placement

### Phase 1: Element Collection

```csharp
ElementCollector.GetTaggableElements(categories)
  → FilteredElementCollector (view scope)
  → Filter by selected categories
  → Calculate view-space bounding boxes
  → Check existing tags
  → Return List<TaggableElement>
```

### Phase 2: Placement Calculation

```csharp
TagPlacementService.CalculatePlacements(elements, settings)
  → Sort elements top-left to bottom-right
  → For each element:
      → Generate 9 candidate positions
      → Score each candidate:
          - Collision penalty (100 pts per collision)
          - Position preference (0-15 pts)
          - Leader length (2 pts per foot)
          - Distance from center (0.5 pts per foot)
          - Alignment bonus (-5 pts for grid alignment)
      → Select lowest score position
      → Add to spatial index
  → Optional: Align tags in rows
```

### Phase 3: Collision Resolution

```csharp
TagPlacementService.ResolveCollisions(placements)
  → For each pair of overlapping tags:
      → Calculate overlap direction
      → Push apart in direction of least resistance
  → Repeat until no collisions or max iterations
```

### Phase 4: Tag Creation

```csharp
TagCreationService.CreateTags(placements, settings)
  → Start Transaction
  → For each placement:
      → Get default tag type for category
      → Convert 2D view coords to 3D model point
      → IndependentTag.Create()
      → Set leader if enabled
  → Commit Transaction
```

## Scoring Function

```
Score = Σ(collision_penalty)    // 100 per collision with existing tag
      + Σ(element_collision)    // 50 per collision with element
      + position_preference     // 0-15 based on preferred position
      + leader_length × 2       // Prefer shorter leaders
      + distance_from_center × 0.5
      - alignment_bonus × 5     // Reward grid alignment
```

## Future: Full Mode (Cloud AI)

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│   Revit      │────▶│  Cloud API   │────▶│  AI Model    │
│   (Context)  │     │  (REST)      │     │  (Python)    │
└──────────────┘     └──────────────┘     └──────────────┘
       │                                         │
       │         ViewContextSnapshot             │
       │  ┌─────────────────────────────┐       │
       │  │ - View bounds               │       │
       │  │ - Element positions         │       │
       │  │ - Existing tags             │       │
       │  │ - Scale, orientation        │       │
       │  └─────────────────────────────┘       │
       │                                         │
       │◀────────────────────────────────────────│
       │         PlacementResult                 │
       │  ┌─────────────────────────────┐       │
       │  │ - Tag locations             │       │
       │  │ - Leader configurations     │       │
       │  │ - Confidence scores         │       │
       │  └─────────────────────────────┘       │
       │                                         │
```

## File Structure

```
src/SmartTag/
├── Entry.cs                       # DLL entry point
├── SmartTagViewModel.cs           # Main ViewModel
├── SmartTag.csproj                # Project file
├── ARCHITECTURE.md                # This file
│
├── Models/
│   └── TagModels.cs               # Data models
│
├── Services/
│   ├── ElementCollector.cs        # Element & tag collection
│   ├── SpatialIndex.cs            # Grid-based spatial index
│   ├── TagPlacementService.cs     # Placement algorithm
│   └── TagCreationService.cs      # Tag creation
│
├── Handlers/
│   └── SmartTagHandler.cs         # ExternalEvent handler
│
└── Views/
    ├── SmartTagWindow.xaml        # Main window UI
    └── SmartTagWindow.xaml.cs     # Code-behind
```

## Key Design Decisions

### 1. Quick Mode First

Local heuristic algorithm runs in < 1 second for most views.
AI/Cloud mode can be added later without changing architecture.

### 2. View-Space Coordinates

All calculations done in 2D view coordinates:
- Simpler collision detection
- View-specific (floor plan vs section)
- Matches how users see the drawing

### 3. Spatial Index

Grid-based spatial index for O(1) collision queries:
- 2-foot cells by default
- Fast enough for 1000+ elements

### 4. Greedy Placement with Scoring

Score-based candidate selection:
- Considers multiple factors
- Easy to tune weights
- Deterministic results

### 5. MVVM + ExternalEvent

Strict separation of concerns:
- ViewModel never touches Revit API
- Handler runs on Revit thread only
- UI stays responsive
