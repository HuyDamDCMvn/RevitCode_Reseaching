# Luồng suy nghĩ khi đặt tag (Tag Placement Flow)

Tài liệu mô tả cách thuật toán Smart Tag quyết định **đặt tag ở đâu** cho từng element trong view.

---

## Tổng quan

Luồng gồm **4 giai đoạn** chính:

1. **Chuẩn bị** – Load rules/patterns/learned, index elements & obstacles  
2. **Chọn vị trí** – Sinh candidate, chấm điểm, chọn vị trí tốt nhất cho từng element  
3. **Giải va chạm** – Đẩy tag ra khỏi annotation/clearance, rồi đẩy tag–tag tránh overlap  
4. **Căn chỉnh** – Căn tag theo hàng/cột (nếu bật “Align tags in rows”)

---

## 1. Chuẩn bị (Handler + TagPlacementService.Initialize)

- **Handler** (Revit thread):
  - Lấy danh sách element cần tag theo category user chọn.
  - Lấy **existing tags** (tag đã có trong view) và **annotations** (dimension, text note, **ClearanceZone**).
- **TagPlacementService.Initialize(elements, existingTags, annotations)**:
  - Build **spatial index** cho:
    - **Element** (để kiểm tra tag có chồng lên element khác không).
    - **Tag** (tag hiện có + tag vừa đặt – để tránh tag chồng tag).
    - **Annotation** (dimension, text, ClearanceZone – tag không được chồng lên).
  - Calibration: kích thước tag (width/height), leader length, min spacing (từ regression + Revit API).

---

## 2. Chọn vị trí cho từng element (CalculatePlacements)

### 2.1 Thứ tự xử lý element

- Lọc element trong group (theo `ShouldTagGroupedElement`).
- **Sắp xếp**: theo **Y giảm dần** (trên trước), rồi **X tăng dần** (trái trước) → xử lý từ trên xuống, trái sang phải.
- Bỏ qua element đã có tag nếu user bật “Skip already tagged”.

### 2.2 Hai loại element

- **Linear dài** (ống/duct dài hơn ngưỡng): **CreateLinearElementPlacements**
  - Chia thành nhiều segment, mỗi segment một tag dọc theo centerline.
  - Mỗi segment: sinh candidate quanh điểm giữa segment, chấm điểm, chọn vị trí tốt nhất.
- **Còn lại** (equipment, fitting, linear ngắn…): **FindBestPlacement**
  - Một tag cho một element.

### 2.3 Sinh candidate (GenerateCandidatePositions)

- **Vị trí ưu tiên** lấy từ (theo thứ tự):
  1. **Rule** (Data/Rules/Tagging) – theo category/family/system.
  2. **Pattern** (Data/Patterns/TagPositions) – theo category/system, scale.
  3. **Learned** (learned_overrides.json) – từ export của user.
- **Khoảng cách** (offset): từ rule/learned hoặc mặc định (leader length + tag size + spacing).
- **Candidate** = các vị trí: TopLeft, TopCenter, TopRight, Left, Center, Right, BottomLeft, BottomCenter, BottomRight (có thể thêm tier xa hơn nếu cần).
- **Linear element** (khi rule bật PreferCenterline): candidate nằm dọc centerline, không leader.
- Candidate được **sắp xếp**: ưu tiên đúng thứ tự preferred positions từ rule/pattern/learned, sau đó theo khoảng cách (gần hơn tốt hơn).

### 2.4 Chấm điểm (ScorePlacement) – điểm thấp = tốt hơn

| Thành phần | Cách tính | Mục đích |
|------------|-----------|----------|
| **Tag–tag collision** | Penalty rất lớn × 3^(số collision) | Ưu tiên không chồng tag khác |
| **Diện tích overlap** | + overlap area × 200 | Tránh overlap càng nhiều càng tốt |
| **Tag–element** | + số element bị chồng × ELEMENT_COLLISION_PENALTY | Tránh tag đè lên element khác |
| **Rule AvoidCategories** | Penalty thêm nếu chồng category rule cấm | Tôn trọng rule “tránh” category |
| **Tag–annotation/ClearanceZone** | + số collision × ANNOTATION_COLLISION_PENALTY | Tránh đè dimension, text, vùng clearance |
| **Leader collision** | + số collision × LEADER_COLLISION_PENALTY | Leader không cắt tag/element |
| **Vị trí user chọn** | Preference score (theo dropdown Position) | Ưu tiên TopRight/Left/… nếu user chọn |
| **Rule/learned preferred position** | − PreferenceBonus nếu trùng vị trí ưu tiên | Ưu tiên đúng vị trí rule/learned |
| **Leader length** | + độ dài leader × 1 | Tag gần element hơn tốt hơn (nhẹ) |
| **Linear, no leader** | − 20 (bonus) | Ưu tiên tag dọc centerline không leader |
| **Distance tier** | + distance multiplier × 5 (khi không collision) | Ưu tiên vị trí gần hơn |
| **Alignment với grid** | − alignment score × AlignmentBonus/10 | Ưu tiên tag nằm trên lưới (1 ft) |
| **Learned alignment** | AlignmentBonus tăng nếu learned có preferAlignRow/Column | Càng ưu tiên align khi đã học từ export |
| **Near-edge (equipment)** | Bonus nếu tag gần mép element | Tag không bay xa quá |

### 2.5 Chọn vị trí tốt nhất (FindBestPlacement)

- Với mỗi candidate:
  - Kiểm tra **có collision với tag** (trong index) không.
  - Chấm điểm bằng **ScorePlacement**.
- **Tách** candidate thành hai nhóm: **không collision** và **có collision**.
- **Ưu tiên**: luôn chọn **candidate không collision có điểm thấp nhất**.
- Chỉ khi **không còn** candidate không collision mới xét candidate có collision (điểm thấp nhất).
- Nếu điểm tốt nhất vẫn **> MAX_ACCEPTABLE_SCORE** → **bỏ qua** element (không đặt tag).
- Sau khi chọn, **thêm** tag đó vào **tag index** để các element sau không đè lên.

---

## 3. Giải va chạm (ResolveCollisions)

Sau khi có danh sách placement cho tất cả element:

### 3.1 Đẩy ra khỏi annotation / ClearanceZone

- Với mỗi placement, kiểm tra **chồng** với _annotationIndex (dimension, text, **ClearanceZone**).
- Nếu chồng: **PushAwayFromAnnotation** – đẩy tag theo hướng từ tâm vùng annotation ra ngoài, một đoạn đủ để hết overlap + margin.
- Lặp tối đa 5 lần để ổn định.

### 3.2 Đẩy tag–tag (PushApart)

- Dùng spatial index của **các placement**.
- Tìm cặp placement có **EstimatedTagBounds** giao nhau.
- **PushApart**: tính hướng và khoảng cách cần đẩy (half-width/height + spacing), đẩy hai tag ra hai phía.
- Lặp tối đa 20 lần đến khi không còn overlap.

---

## 4. Vòng lặp tinh chỉnh (RefinePlacementsIterative)

Sau ResolveCollisions, chạy **tối đa 3 lần**:

1. **Resolve + Align lại**: gọi lại ResolveCollisions (đẩy annotation, đẩy tag–tag), rồi AlignTagPlacements nếu bật "Align tags in rows".
2. **Cập nhật _tagIndex**: đồng bộ index với bounds hiện tại của từng placement (sau khi đã đẩy/căn).
3. **Quét overlap**: tìm placement nào vẫn còn chồng tag khác hoặc chồng annotation.
4. **Đặt lại (re-place)**:
   - Chỉ xét placement thuộc element **chỉ có 1 tag** (bỏ qua linear nhiều segment).
   - Với mỗi placement còn overlap: xóa nó khỏi _tagIndex, gọi **FindBestPlacement** lại cho element đó (với trạng thái tag index hiện tại), nếu có vị trí mới thì thay placement trong list và thêm vào _tagIndex; không thì giữ nguyên và thêm lại vào index.
5. Nếu không còn placement nào overlap → thoát vòng; nếu hết 3 lần thì dừng.

Kết quả: tag còn overlap sau bước 3 có cơ hội được **đặt lại** vị trí khác hoặc được **căn chỉnh** thêm qua Resolve/Align trong lần lặp sau.

---

## 5. Căn chỉnh (AlignTagPlacements)

Chỉ chạy khi user bật **“Align tags in rows”**:

- **Hàng (row)** – gộp theo Y (tolerance = ~2× chiều cao tag): trong mỗi hàng ≥ 2 tag thì căn Y về trung bình nếu không gây collision → tag cùng hàng có cùng Y.
- **Cột (column)** – theo bản mẫu (EA/SA xếp dọc): gộp theo X (tolerance = ~2× chiều rộng tag), trong mỗi cột ≥ 2 tag thì căn X về trung bình nếu không gây collision → tag cùng cột xếp dọc (stack như EA-350x250 / SA-350x250).

Scoring có **bonus căn cột**: candidate có X gần tag khác (cùng cột, không overlap) được giảm điểm để ưu tiên stack dọc.

---

## 6. Tóm tắt thứ tự quyết định

1. **Rule / Pattern / Learned** → vị trí ưu tiên (TopRight, BottomCenter, …) và offset/leader/alignment bonus.  
2. **Sinh candidate** quanh element theo các vị trí đó, nhiều khoảng cách.  
3. **Chấm điểm** từng candidate (collision nặng nhất, rồi preference, alignment, distance).  
4. **Chọn** candidate **không collision** tốt nhất; không có thì mới chọn có collision.  
5. **Cập nhật tag index** để element tiếp theo tránh đè.  
6. Sau khi chọn xong mọi element: **đẩy** tag khỏi annotation/clearance, rồi **đẩy** tag–tag.  
7. **Vòng tinh chỉnh** (tối đa 3 lần): resolve + align lại → quét placement còn overlap → **đặt lại** (re-place) cho từng tag overlap (chỉ element 1 tag) → lặp đến khi sạch hoặc hết lần.  
8. Nếu bật align: **căn** tag theo hàng (cùng Y) khi an toàn.

Toàn bộ luồng đảm bảo: **tránh overlap** (tag, element, dimension, ClearanceZone) là ưu tiên cao nhất; sau đó mới tối ưu **vị trí ưa thích** và **alignment** theo rule/pattern/learned và thiết lập user.
