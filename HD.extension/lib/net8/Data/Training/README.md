# Training Data (Future Use)

This folder will contain processed data for ML training when transitioning to Option B.

## Not Yet Implemented — Plan

### Features (inputs)
- Element bounding box (normalized)
- Element category one-hot encoding
- Nearby element positions
- View type
- View scale
- Existing tag density
- Distance to edges/grids

### Labels (outputs)
- Tag position (x, y) relative to element center
- Position type classification
- Leader required (boolean)

## Data Pipeline (when implemented)

```
Feedback/*.json → feature_extraction.py → Training/features/*.npy
                                        → Training/labels/*.npy
```

## Model Options

1. **Simple**: Decision tree/Random forest for position classification
2. **Medium**: Gradient boosting (XGBoost/LightGBM)
3. **Advanced**: Small neural network for regression

## When to Start?

Transition to ML training when:
- [ ] At least 500 feedback samples collected
- [ ] Option C rules are stable
- [ ] Clear patterns emerge from data analysis
