# SmartTag ML Placement Engine - Implementation Roadmap

## Overview

Implementation of a **CSP + KNN + RL** system to optimize automatic tag placement positions.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           SmartTag Placement Engine                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  Input ──▶ [KNN Match] ──▶ [RL Refine] ──▶ [CSP Solve] ──▶ Output          │
│              Phase 1        Phase 2        Phase 3                          │
│                                                                             │
│  + User Feedback Loop ──▶ Continuous Learning                               │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Phase 1: CSP Solver + Improved Rules (Weeks 1-2)

### Goal
- Implement CSP solver ensuring no collisions
- Improve rule-based initial positions
- Achieve **70-80% accuracy** compared to reference drawings

### Tasks

#### 1.1 CSP Solver Core
**File**: `src/SmartTag/ML/CSPSolver.cs`

```csharp
public class CSPSolver
{
    // Hard Constraints (MUST satisfy)
    public bool CheckNoTagOverlap(TagPlacement tag, List<TagPlacement> existing);
    public bool CheckNoElementOverlap(TagPlacement tag, List<BoundingBox2D> elements);
    public bool CheckLeaderNoCollision(TagPlacement tag, List<BoundingBox2D> elements);
    public bool CheckWithinViewCrop(TagPlacement tag, BoundingBox2D viewCrop);
    
    // Soft Constraints (Optimize)
    public double ScoreAlignment(List<TagPlacement> tags);
    public double ScoreLeaderLength(TagPlacement tag);
    public double ScoreSpacingConsistency(List<TagPlacement> tags);
    
    // Solver
    public List<TagPlacement> Solve(List<TagCandidate> candidates, CSPConstraints constraints);
}
```

#### 1.2 Alignment Detection
**File**: `src/SmartTag/ML/AlignmentDetector.cs`

```csharp
public class AlignmentDetector
{
    public List<AlignmentLine> DetectHorizontalAlignments(List<TagPlacement> tags, double tolerance);
    public List<AlignmentLine> DetectVerticalAlignments(List<TagPlacement> tags, double tolerance);
    public AlignmentSuggestion SuggestAlignment(Point2D position, List<AlignmentLine> alignments);
}
```

#### 1.3 Context-Aware Rules
**File**: `src/SmartTag/ML/ContextAnalyzer.cs`

```csharp
public class ContextAnalyzer
{
    public ElementContext Analyze(TaggableElement element, List<TaggableElement> neighbors);
    // Context includes:
    // - Orientation (horizontal/vertical/diagonal)
    // - Density (low/medium/high)
    // - Neighbors (above/below/left/right)
    // - Distance to boundaries
}
```

#### 1.4 Deliverables
- [ ] `CSPSolver.cs` - Core solver with backtracking
- [ ] `AlignmentDetector.cs` - Row/column detection
- [ ] `ContextAnalyzer.cs` - Element context extraction
- [ ] `CSPConstraints.cs` - Constraint definitions
- [ ] Unit tests with 50+ test cases

---

## Phase 2: KNN Template Matching (Weeks 3-4)

### Goal
- Annotate training data from reference drawings
- Implement KNN matching
- Achieve **85-90% accuracy**

### Tasks

#### 2.1 Training Data Schema
**File**: `src/SmartTag/Data/Schema/TrainingData.schema.json`

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "samples": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "id": { "type": "string" },
          "source": {
            "type": "object",
            "properties": {
              "project": { "type": "string" },
              "drawing": { "type": "string" },
              "viewScale": { "type": "integer" }
            }
          },
          "element": {
            "type": "object",
            "properties": {
              "category": { "type": "string" },
              "familyName": { "type": "string" },
              "orientation": { "type": "number" },
              "length": { "type": "number" },
              "width": { "type": "number" },
              "height": { "type": "number" }
            }
          },
          "context": {
            "type": "object",
            "properties": {
              "density": { "type": "string", "enum": ["low", "medium", "high"] },
              "neighborAbove": { "type": "boolean" },
              "neighborBelow": { "type": "boolean" },
              "neighborLeft": { "type": "boolean" },
              "neighborRight": { "type": "boolean" },
              "distanceToWall": { "type": "number" }
            }
          },
          "tag": {
            "type": "object",
            "properties": {
              "position": { "type": "string" },
              "offsetX": { "type": "number" },
              "offsetY": { "type": "number" },
              "hasLeader": { "type": "boolean" },
              "alignedWithRow": { "type": "boolean" },
              "alignedWithColumn": { "type": "boolean" }
            }
          }
        }
      }
    }
  }
}
```

#### 2.2 Feature Extractor
**File**: `src/SmartTag/ML/FeatureExtractor.cs`

```csharp
public class FeatureExtractor
{
    public float[] ExtractFeatures(TaggableElement element, ElementContext context);
    // Feature vector (20 dimensions):
    // [0-5]   Category one-hot (Pipe, Duct, Equipment, CableTray, Fitting, Other)
    // [6]     Orientation angle (normalized 0-1)
    // [7]     Length (normalized)
    // [8]     Width (normalized)
    // [9]     Height (normalized)
    // [10]    Density (0=low, 0.5=medium, 1=high)
    // [11-14] Neighbors (above, below, left, right)
    // [15]    Distance to wall (normalized)
    // [16]    View scale (normalized)
    // [17-19] Reserved for future
}
```

#### 2.3 KNN Matcher
**File**: `src/SmartTag/ML/KNNMatcher.cs`

```csharp
public class KNNMatcher
{
    private readonly KDTree<TrainingSample> _kdTree;
    public void LoadTrainingData(string path);
    public List<(TrainingSample sample, double distance)> FindKNearest(float[] features, int k = 5);
    public TagPositionVote Vote(List<(TrainingSample sample, double distance)> neighbors);
}
```

#### 2.4 Annotation Tool
**File**: `tools/AnnotationHelper/`

Python script to annotate training data from Revit exports:
```python
# Extract tag positions from Revit views
# Generate training samples in JSON format
# Validate against schema
```

#### 2.5 Training Data Collection
**Target**: 500+ annotated samples

| Source | Samples | Categories |
|--------|---------|------------|
| MunichRE LP5 (1:50) | 200 | HVAC, Sanitary, Heating |
| Munich Arena (1:100) | 200 | Electrical, Cable Tray |
| Munich Arena (1:200) | 100 | Overview layouts |

#### 2.6 Deliverables
- [ ] `TrainingData.schema.json` - Data schema
- [ ] `FeatureExtractor.cs` - Feature extraction
- [ ] `KNNMatcher.cs` - KNN with KD-Tree
- [ ] `AnnotationHelper/` - Python annotation tool
- [ ] `Data/Training/annotated/` - 500+ samples
- [ ] Integration tests

---

## Phase 3: RL Integration (Weeks 5-6)

### Goal
- Train DQN network with user feedback
- Integrate ONNX runtime
- Achieve **90-95% accuracy**

### Tasks

#### 3.1 RL Environment
**File**: `src/SmartTag/ML/RLEnvironment.cs`

```csharp
public class RLEnvironment
{
    // State vector (50 dimensions):
    // [0-19]  Element features (from FeatureExtractor)
    // [20-24] KNN candidate scores (top 5)
    // [25-34] Nearest 5 existing tag positions (relative)
    // [35-44] Collision map (10 directions, 0=free, 1=blocked)
    // [45-49] Alignment opportunities (5 nearest lines)
    
    public float[] GetState(TaggableElement element, List<TagPlacement> existingTags);
    
    public enum Action
    {
        SelectCandidate0, SelectCandidate1, SelectCandidate2,
        SelectCandidate3, SelectCandidate4,
        ShiftLeft, ShiftRight, ShiftUp, ShiftDown,
        AlignToRow, AlignToColumn, ToggleLeader
    }
    
    public double CalculateReward(TagPlacement placement, bool userApproved);
}
```

#### 3.2 Policy Network (Python Training)
**File**: `tools/RLTraining/train_dqn.py`

```python
import torch
import torch.nn as nn

class DQN(nn.Module):
    def __init__(self, state_dim=50, action_dim=12):
        super().__init__()
        self.fc1 = nn.Linear(state_dim, 128)
        self.fc2 = nn.Linear(128, 256)
        self.fc3 = nn.Linear(256, 256)
        self.fc4 = nn.Linear(256, action_dim)
        
    def forward(self, x):
        x = torch.relu(self.fc1(x))
        x = torch.relu(self.fc2(x))
        x = torch.relu(self.fc3(x))
        return self.fc4(x)

# Training loop with experience replay
# Export to ONNX after training
```

#### 3.3 ONNX Inference
**File**: `src/SmartTag/ML/RLAgent.cs`

```csharp
public class RLAgent
{
    private readonly InferenceSession _session;
    
    public RLAgent(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }
    
    public RLAction GetAction(float[] state);
    public RLAction GetActionWithExploration(float[] state, double epsilon);
}
```

#### 3.4 Reward System
**File**: `src/SmartTag/ML/RewardCalculator.cs`

```csharp
public class RewardCalculator
{
    // Immediate rewards (automatic)
    public double NoCollision => +10.0;
    public double AlignedWithExisting => +5.0;
    public double ShortLeader => +3.0;
    public double CollisionWithTag => -20.0;
    public double CollisionWithElement => -15.0;
    public double LeaderCrossesElement => -10.0;
    
    // Delayed rewards (from user)
    public double UserApproved => +50.0;
    public double UserRejected => -50.0;
    public double UserAdjusted => -10.0;
}
```

#### 3.5 NuGet Dependencies
```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.16.0" />
```

#### 3.6 Deliverables
- [ ] `RLEnvironment.cs` - State/action/reward definitions
- [ ] `RLAgent.cs` - ONNX inference wrapper
- [ ] `RewardCalculator.cs` - Reward function
- [ ] `tools/RLTraining/` - Python training scripts
- [ ] `models/placement_policy.onnx` - Trained model (~5MB)
- [ ] Integration with main pipeline

---

## Phase 4: Feedback System (Week 7)

### Goal
- UI for approve/reject/adjust
- Collect feedback for continuous learning
- Set up incremental training pipeline

### Tasks

#### 4.1 Preview UI Enhancement
**File**: `src/SmartTag/Views/PreviewPanel.xaml`

```xml
<ItemsControl ItemsSource="{Binding PreviewPlacements}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding ElementName}" />
                <TextBlock Text="{Binding Position}" />
                <Button Content="✓" Command="{Binding ApproveCommand}" />
                <Button Content="✗" Command="{Binding RejectCommand}" />
                <Button Content="↔" Command="{Binding AdjustCommand}" />
            </StackPanel>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

#### 4.2 Feedback Collector
**File**: `src/SmartTag/ML/FeedbackCollector.cs`

```csharp
public class FeedbackCollector
{
    public void RecordFeedback(TagPlacement placement, FeedbackType type, Point2D? adjustedPosition);
    public void SaveBatch(string path);
    public static List<FeedbackRecord> LoadFeedback(string path);
}

public enum FeedbackType { Approved, Rejected, Adjusted }
```

#### 4.3 Incremental Training Pipeline
**File**: `tools/IncrementalTraining/`

```python
# 1. Load existing model
# 2. Load new feedback data
# 3. Generate training samples
# 4. Fine-tune model
# 5. Export updated ONNX
# 6. Validate improvement
```

#### 4.4 Deliverables
- [ ] `PreviewPanel.xaml` - Enhanced UI
- [ ] `FeedbackCollector.cs` - Data collection
- [ ] `FeedbackRecord.cs` - Data model
- [ ] `tools/IncrementalTraining/` - Retraining scripts
- [ ] Auto-backup mechanism for feedback data

---

## Phase 5: Integration & Testing (Week 8)

### Tasks

#### 5.1 Pipeline Integration
**File**: `src/SmartTag/ML/PlacementEngine.cs`

```csharp
public class PlacementEngine
{
    private readonly KNNMatcher _knn;
    private readonly RLAgent _rl;
    private readonly CSPSolver _csp;
    
    public List<TagPlacement> CalculatePlacements(
        List<TaggableElement> elements, TagSettings settings)
    {
        var placements = new List<TagPlacement>();
        foreach (var element in elements)
        {
            var features = _featureExtractor.Extract(element);
            var candidates = _knn.FindKNearest(features, k: 5);
            var state = _env.GetState(element, placements);
            var action = _rl.GetAction(state);
            var refinedPosition = ApplyAction(candidates, action);
            var finalPosition = _csp.Solve(refinedPosition, placements);
            placements.Add(finalPosition);
        }
        return placements;
    }
}
```

#### 5.2 A/B Testing Framework
```csharp
public class ABTestFramework
{
    public TestResult Compare(List<TaggableElement> elements, TagSettings settings);
    // Metrics: Collision count, Alignment score, Average leader length, User approval rate
}
```

#### 5.3 Performance Benchmarks
- Target: < 500ms for 100 elements
- Memory: < 100MB additional

#### 5.4 Deliverables
- [ ] `PlacementEngine.cs` - Integrated pipeline
- [ ] `ABTestFramework.cs` - Testing framework
- [ ] Performance benchmarks
- [ ] 95% test coverage
- [ ] Documentation

---

## File Structure

```
src/SmartTag/
├── ML/
│   ├── CSPSolver.cs
│   ├── AlignmentDetector.cs
│   ├── ContextAnalyzer.cs
│   ├── CSPConstraints.cs
│   ├── FeatureExtractor.cs
│   ├── KNNMatcher.cs
│   ├── KDTree.cs
│   ├── RLEnvironment.cs
│   ├── RLAgent.cs
│   ├── RewardCalculator.cs
│   ├── FeedbackCollector.cs
│   ├── PlacementEngine.cs
│   └── ABTestFramework.cs
├── Data/
│   ├── Schema/
│   │   └── TrainingData.schema.json
│   ├── Training/
│   │   ├── annotated/
│   │   └── feedback/
│   └── Models/
│       └── placement_policy.onnx
└── Views/
    └── PreviewPanel.xaml

tools/
├── AnnotationHelper/
├── RLTraining/
└── IncrementalTraining/
```

---

## Timeline Summary

| Phase | Duration | Output | Accuracy |
|-------|----------|--------|----------|
| Phase 1: CSP + Rules | 2 weeks | Constraint solver | 70-80% |
| Phase 2: KNN | 2 weeks | Template matching | 85-90% |
| Phase 3: RL | 2 weeks | Policy network | 90-95% |
| Phase 4: Feedback | 1 week | Continuous learning | - |
| Phase 5: Integration | 1 week | Production ready | 95%+ |
| **Total** | **8 weeks** | | |

---

## Dependencies

### C# (.NET 8)
```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.16.0" />
```

### Python (Training)
```
torch>=2.0
onnx>=1.14
scikit-learn>=1.3
numpy>=1.24
```

---

## Success Metrics

| Metric | Phase 1 | Phase 2 | Phase 3 | Target |
|--------|---------|---------|---------|--------|
| Collision Rate | < 5% | < 2% | < 1% | < 1% |
| Alignment Score | 60% | 80% | 90% | > 90% |
| User Approval | 70% | 85% | 95% | > 95% |
| Processing Time | 200ms | 300ms | 500ms | < 500ms |

---

## Next Steps

1. **Immediate**: Start Phase 1 - CSP Solver implementation
2. **Parallel**: Begin training data annotation
3. **Week 3**: Start KNN implementation
4. **Week 5**: Set up Python training environment

---

*Last updated: 2026-02-13*
