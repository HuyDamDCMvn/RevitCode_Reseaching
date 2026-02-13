# Training Data (Future Use)

Thư mục này sẽ chứa processed data cho ML training khi chuyển sang Option B.

## Chưa implement - Kế hoạch

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

## Data Pipeline (khi implement)

```
Feedback/*.json → feature_extraction.py → Training/features/*.npy
                                        → Training/labels/*.npy
```

## Model Options

1. **Simple**: Decision tree/Random forest for position classification
2. **Medium**: Gradient boosting (XGBoost/LightGBM)
3. **Advanced**: Small neural network for regression

## Khi nào bắt đầu?

Chuyển sang ML training khi:
- [ ] Có ít nhất 500 feedback samples
- [ ] Option C rules đã ổn định
- [ ] Có patterns rõ ràng từ data analysis
