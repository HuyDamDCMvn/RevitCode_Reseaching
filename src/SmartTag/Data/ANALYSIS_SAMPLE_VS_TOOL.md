# Phân tích: Bản mẫu vs Output tool

So sánh bản vẽ mẫu (chuẩn) với output Smart Tag để cập nhật thuật toán training/placement.

---

## Bản mẫu (reference)

- **Tag**: dạng PREFIX-WIDTHxHEIGHT (EA-350x250, SA-200x150).
- **Vị trí**: gần đoạn ống, thường **trên** (blue) hoặc **trên/dưới** (red).
- **Cặp EA/SA**: hai tag **xếp dọc** (cùng X), một trên một dưới, cùng gắn vào một đoạn/điểm.
- **Cột**: nhiều cặp tag cùng vùng X tạo thành **cột thẳng** (cùng X), dễ đọc.
- **Leader**: ngắn, nối từ tag xuống ống; một số leader cắt ống/leader khác khi mật độ cao.

---

## Output tool (trước cập nhật)

- **Align tags in rows**: tag cùng hàng (cùng Y) → hàng ngang gọn.
- **Thiếu căn cột**: tag gần nhau theo X chưa được kéo về cùng X → ít khi stack dọc như EA/SA.
- Vùng đông tag vẫn dễ **dính nhau** hoặc **chồng nhẹ** dù đã resolve/refinement.

---

## Cập nhật thuật toán (đã làm)

1. **Căn cột (column alignment)** trong `AlignTagPlacements`:
   - Sau khi căn hàng (Y), gộp tag theo **X** (tolerance ~ 2× chiều rộng tag).
   - Trong mỗi cột có ≥ 2 tag: căn **X** về trung bình nếu không gây collision.
   - Kết quả: tag cùng vùng X xếp **dọc** (cùng cột), giống cặp EA/SA trong mẫu.

2. **Bonus scoring “cùng cột”** trong `ScorePlacement`:
   - Nếu vị trí candidate có **X gần** với tag khác (trong band ~1.5 ft) và **không overlap** → giảm điểm (bonus).
   - Ưu tiên chọn vị trí tạo **stack dọc** với tag đã đặt.

3. **Rule HVAC ducts** (đã có): `preferredPositions: ["Center","TopCenter","BottomCenter"]`, `groupAlignment: "AlongCenterline"` – giữ nguyên, phù hợp ống và bản mẫu.

---

## Kết quả mong đợi

- Tag trên cùng đoạn/ống có xu hướng **cùng cột** (cùng X), xếp dọc.
- Hàng (cùng Y) vẫn giữ nhờ căn hàng hiện có.
- Kết hợp **hàng + cột** gần với bố cục bản mẫu (EA/SA stack, cột thẳng).

---

## Gợi ý training thêm

- **Export** view đã chỉnh tay đúng ý (cột/hàng như mẫu) → dùng **Export training data from view**.
- Chạy **ingest** (tự động sau export hoặc `tools/ingest_annotated_to_learned.py`) để cập nhật **preferAlignRow** / **preferAlignColumn** trong learned_overrides.
- Learned alignment đã được dùng để tăng **AlignmentBonus** khi đặt tag.
