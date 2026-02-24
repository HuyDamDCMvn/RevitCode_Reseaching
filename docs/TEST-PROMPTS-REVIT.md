# Test Prompts cho Revit Chatbot

> Mở chatbot trong Revit, gõ từng prompt theo thứ tự trong mỗi Test Case.
> Sau mỗi chuỗi hội thoại, nhấn **Clear Chat** rồi chuyển sang Test Case tiếp theo.
> Đánh giá: ✅ Đúng | ⚠️ Gần đúng | ❌ Sai

---

## A. TEST ĐƠN LẺ — Kiểm tra nhận diện cơ bản

Gõ từng prompt riêng lẻ (Clear Chat giữa mỗi prompt):

### A1. Intent Recognition (EN)
| # | Prompt | Expected Intent | Expected Category |
|---|--------|----------------|-------------------|
| 1 | `list all ducts on level 1` | Query | Ducts |
| 2 | `how many pipes on level 2` | Count | Pipes |
| 3 | `check duct velocity` | Check | Ducts |
| 4 | `resize pipes to 150mm` | Modify | Pipes |
| 5 | `export ducts to CSV` | Export | Ducts |
| 6 | `color pipes by system` | Visual | Pipes |
| 7 | `connect 2 selected ducts` | Connect | Ducts |
| 8 | `tag all untagged pipes` | Tag | Pipes |
| 9 | `delete selected elements` | Delete | — |
| 10 | `what can you do` | Help | — |

### A2. Intent Recognition (VN)
| # | Prompt | Expected Intent | Expected Category |
|---|--------|----------------|-------------------|
| 1 | `liệt kê ống gió tầng 1` | Query | Ducts |
| 2 | `đếm ống nước tầng 2` | Count | Pipes |
| 3 | `kiểm tra vận tốc ống gió` | Check | Ducts |
| 4 | `đổi kích thước ống nước sang 150mm` | Modify | Pipes |
| 5 | `xuất ống gió ra CSV` | Export | Ducts |
| 6 | `tô màu ống nước theo hệ thống` | Visual | Pipes |
| 7 | `nối 2 ống gió đang chọn` | Connect | Ducts |
| 8 | `ghi chú ống nước chưa tag` | Tag | Pipes |
| 9 | `xóa phần tử đang chọn` | Delete | — |
| 10 | `bạn có thể làm gì` | Help | — |

### A3. Numeric + Unit
| # | Prompt | Expected | Kiểm tra |
|---|--------|----------|----------|
| 1 | `check velocity max 5 m/s` | Check, max_velocity=5 | Số + đơn vị |
| 2 | `resize ducts to 400mm` | Modify, diameter=400 | mm |
| 3 | `set pipe slope 2%` | Modify, slope=2 | % |
| 4 | `add insulation 25mm` | Modify, thickness=25 | mm |
| 5 | `split pipes every 1350mm` | Modify, segment=1350 | mm |
| 6 | `auto size duct at 4 m/s` | Modify, velocity=4 | m/s |
| 7 | `offset pipes 2500mm` | Modify, offset=2500 | mm |
| 8 | `resize duct to 500x300mm` | Modify, w=500 h=300 | WxH |

### A4. System Detection
| # | Prompt | Expected System |
|---|--------|----------------|
| 1 | `list SA ducts` | Supply Air |
| 2 | `show CHW pipes` | Chilled Water |
| 3 | `check EA ducts velocity` | Exhaust Air |
| 4 | `count RA ducts` | Return Air |
| 5 | `show HW pipes` | Hot Water |
| 6 | `check FP pipes` | Fire Protection |
| 7 | `liệt kê ống hệ cấp gió` | Supply Air |
| 8 | `thống kê hệ PCCC` | Fire Protection |

### A5. DryRun / Preview
| # | Prompt | Kiểm tra |
|---|--------|----------|
| 1 | `preview resize ducts to 400mm` | DryRun = true |
| 2 | `xem trước đổi size ống gió` | DryRun = true |
| 3 | `simulate delete unused` | DryRun = true |
| 4 | `mô phỏng thay đổi` | DryRun = true |

---

## B. TEST CHUỖI HỘI THOẠI — Kiểm tra Context Carryover

> **QUAN TRỌNG**: Gõ lần lượt từng prompt KHÔNG clear chat giữa các bước.
> Mục đích: kiểm tra chatbot có nhớ ngữ cảnh từ lượt trước không.

### B1. Duct Workflow (EN) — 13 bước
```
Bước 1:  list all ducts on level 1
         → Expect: Query Ducts trên Level 1

Bước 2:  how many are there
         → Expect: Count Ducts (carry từ bước 1, KHÔNG cần nói "ducts")

Bước 3:  summarize by system
         → Expect: Analyze Ducts (carry category)

Bước 4:  check velocity
         → Expect: Check Ducts velocity (carry category)

Bước 5:  which ones exceed 5 m/s
         → Expect: Check + max_velocity=5

Bước 6:  resize those to 400mm
         → Expect: Modify Ducts, diameter=400 (carry category)

Bước 7:  preview first
         → Expect: DryRun=true, carry Modify Ducts

Bước 8:  ok apply the changes
         → Expect: Modify Ducts (execute, không preview)

Bước 9:  now check velocity again
         → Expect: Check Ducts (carry category)

Bước 10: color them by system
         → Expect: Visual Ducts (carry category)

Bước 11: export to CSV
         → Expect: Export Ducts (carry category)

Bước 12: do the same for pipes
         → Expect: Export Pipes (switch category, carry intent)

Bước 13: how many pipes total
         → Expect: Count Pipes
```

### B2. Pipe Workflow (VN) — 13 bước
```
Bước 1:  liệt kê ống nước tầng 1
         → Expect: Query Pipes Level 1

Bước 2:  đếm bao nhiêu
         → Expect: Count Pipes (carry từ bước 1)

Bước 3:  thống kê theo hệ thống
         → Expect: Analyze Pipes (carry category)

Bước 4:  kiểm tra độ dốc
         → Expect: Check Pipes slope

Bước 5:  đặt độ dốc 2%
         → Expect: Modify Pipes, slope=2%

Bước 6:  xem trước thay đổi
         → Expect: DryRun Modify Pipes

Bước 7:  thực hiện đi
         → Expect: Modify Pipes (execute)

Bước 8:  kiểm tra lại
         → Expect: Check Pipes (carry category)

Bước 9:  tô màu theo hệ thống
         → Expect: Visual Pipes

Bước 10: xuất ra CSV
         → Expect: Export Pipes

Bước 11: tương tự cho ống gió
         → Expect: switch sang Ducts (carry intent Export hoặc Query)

Bước 12: bao nhiêu ống gió
         → Expect: Count Ducts

Bước 13: thống kê ống gió
         → Expect: Analyze Ducts
```

### B3. Multi-System Switch (EN) — 12 bước
```
Bước 1:  list Supply Air ducts
         → Expect: Query Ducts, System=Supply Air

Bước 2:  count them
         → Expect: Count Ducts (carry SA)

Bước 3:  now show Exhaust Air ducts
         → Expect: Query Ducts, System=Exhaust Air

Bước 4:  how many
         → Expect: Count Ducts (carry EA)

Bước 5:  show Return Air ducts too
         → Expect: Query Ducts, System=Return Air

Bước 6:  count those too
         → Expect: Count Ducts (carry RA)

Bước 7:  analyze Supply Air totals
         → Expect: Analyze, System=Supply Air

Bước 8:  same for Exhaust Air
         → Expect: Analyze, System=Exhaust Air ("same" = reference keyword)

Bước 9:  and Return Air too
         → Expect: Analyze, System=Return Air

Bước 10: check velocity for all ducts
         → Expect: Check Ducts velocity

Bước 11: color all ducts by system
         → Expect: Visual Ducts

Bước 12: export comparison to CSV
         → Expect: Export Ducts
```

### B4. Mixed Language (EN↔VN) — 13 bước
```
Bước 1:  list all ducts on level 1
         → EN: Query Ducts Level 1

Bước 2:  đếm bao nhiêu
         → VN: Count Ducts (carry từ EN prompt)

Bước 3:  thống kê theo hệ thống
         → VN: Analyze Ducts (carry)

Bước 4:  check velocity
         → EN: Check Ducts (carry)

Bước 5:  vận tốc tối đa 5 m/s
         → VN: Check, max=5

Bước 6:  resize to 400mm
         → EN: Modify Ducts, 400mm

Bước 7:  xem trước
         → VN: DryRun

Bước 8:  ok do it
         → EN: Execute

Bước 9:  kiểm tra lại
         → VN: Check (carry Ducts)

Bước 10: color by system
         → EN: Visual Ducts

Bước 11: xuất CSV
         → VN: Export Ducts

Bước 12: also export IFC
         → EN: Export IFC

Bước 13: cảm ơn bạn
         → VN: Help/Conversational
```

### B5. Level Switch — 12 bước
```
Bước 1:  list all ducts on level 1
         → Query Ducts L1

Bước 2:  count them
         → Count Ducts L1 (carry)

Bước 3:  check velocity
         → Check Ducts L1 (carry)

Bước 4:  same thing on level 2
         → Query Ducts L2 ("same" = reference keyword, switch level)

Bước 5:  count those
         → Count Ducts L2 (carry)

Bước 6:  check velocity like above
         → Check Ducts L2 ("like above" = reference keyword)

Bước 7:  now level 3 ducts
         → Query Ducts L3

Bước 8:  count again
         → Count Ducts L3 (carry)

Bước 9:  back to level 1 but pipes
         → Query Pipes L1 (switch cả category VÀ level)

Bước 10: count them
         → Count Pipes L1 (carry)

Bước 11: export level 1 data
         → Export L1 (carry)

Bước 12: như trên nhưng tầng 2
         → Export L2 ("như trên" = reference keyword VN)
```

### B6. QA Audit Chain (Mixed) — 12 bước
```
Bước 1:  check model health
         → Check model audit

Bước 2:  kiểm tra va chạm
         → Check clashes

Bước 3:  check disconnected elements
         → Check disconnected

Bước 4:  kiểm tra bảo ôn
         → Check insulation

Bước 5:  how many warnings total
         → Count warnings

Bước 6:  top 10 warnings
         → Check/Query warnings, limit=10

Bước 7:  check velocity for ducts
         → Check Ducts velocity

Bước 8:  kiểm tra độ dốc ống nước
         → Check Pipes slope

Bước 9:  fix the disconnected ducts
         → Modify Ducts (fix)

Bước 10: kiểm tra lại kết nối
         → Check disconnected (verify)

Bước 11: color problematic elements red
         → Visual color

Bước 12: export audit report to CSV
         → Export CSV
```

### B7. Connect & Route — 10 bước
```
Bước 1:  nối 2 ống gió đang chọn
         → Connect Ducts

Bước 2:  kiểm tra kết nối
         → Check Ducts disconnected (carry)

Bước 3:  nối 2 ống nước đang chọn
         → Connect Pipes (switch)

Bước 4:  check connection
         → Check Pipes (carry)

Bước 5:  route duct from FCU to main
         → Connect/Route Ducts

Bước 6:  tránh kết cấu
         → Connect Ducts (carry, avoid structural)

Bước 7:  check velocity after routing
         → Check Ducts velocity (carry)

Bước 8:  connect pipe to equipment
         → Connect Pipes

Bước 9:  tag new connections
         → Tag Pipes (carry)

Bước 10: export to CSV
         → Export (carry)
```

---

## C. TEST ĐẶC BIỆT

### C1. Ambiguous "ống" (VN)
```
Bước 1:  kiểm tra ống hệ cấp gió
         → Expect: Check Ducts (vì "cấp gió" = Supply Air → ống gió)

Bước 2:  liệt kê ống hệ nước lạnh
         → Expect: Query Pipes (vì "nước lạnh" = Chilled Water → ống nước)

Bước 3:  đếm ống hệ thoát nước
         → Expect: Count Pipes (vì "thoát nước" = Sanitary → ống nước)
```

### C2. Numeric Edge Cases
```
Bước 1:  find pipes DN100
         → Expect: Query Pipes (DN notation)

Bước 2:  resize to DN150
         → Expect: Modify Pipes DN150

Bước 3:  find duct ø300
         → Expect: Query Ducts (ø symbol)

Bước 4:  resize duct to 600x400mm
         → Expect: Modify Ducts w=600 h=300

Bước 5:  move up 500mm
         → Expect: Modify, direction=up, distance=500
```

### C3. DryRun → Execute Pattern
```
Bước 1:  liệt kê ống gió tầng 1
         → Query Ducts L1

Bước 2:  xem trước đổi kích thước 400mm
         → DryRun + Modify Ducts 400mm

Bước 3:  thử 500x300 xem sao
         → DryRun + Modify Ducts 500x300

Bước 4:  ok 500x300 được rồi thực hiện
         → Modify Ducts 500x300 (execute, NOT dry run)

Bước 5:  kiểm tra vận tốc
         → Check Ducts velocity (carry)
```

### C4. Self-Training Test
```
Bước 1:  Nhấn nút "Retrain" (nếu có trong UI)
         → Expect: Hiện báo cáo training với:
           - Epoch results
           - Overall accuracy %
           - Conv accuracy % (conversation chains)
           - Examples + weights count

Bước 2:  Sau khi train xong, thử:
         liệt kê ống gió tầng 1
         → So sánh tốc độ và độ chính xác trước/sau training
```

---

## D. BẢNG ĐÁNH GIÁ

Sau khi test, điền kết quả:

| Test Case | Kết quả | Ghi chú |
|-----------|---------|---------|
| A1. Intent EN | ✅/⚠️/❌ | |
| A2. Intent VN | ✅/⚠️/❌ | |
| A3. Numeric | ✅/⚠️/❌ | |
| A4. System | ✅/⚠️/❌ | |
| A5. DryRun | ✅/⚠️/❌ | |
| B1. Duct EN | ✅/⚠️/❌ | |
| B2. Pipe VN | ✅/⚠️/❌ | |
| B3. Multi-System | ✅/⚠️/❌ | |
| B4. Mixed Lang | ✅/⚠️/❌ | |
| B5. Level Switch | ✅/⚠️/❌ | |
| B6. QA Audit | ✅/⚠️/❌ | |
| B7. Connect | ✅/⚠️/❌ | |
| C1. Ambiguous | ✅/⚠️/❌ | |
| C2. Numeric Edge | ✅/⚠️/❌ | |
| C3. DryRun→Exec | ✅/⚠️/❌ | |
| C4. Self-Train | ✅/⚠️/❌ | |

### Tiêu chí đánh giá
- ✅ **Đúng**: Intent + Category + System + Tool đều chính xác
- ⚠️ **Gần đúng**: Intent đúng nhưng category/system sai, hoặc tool không tối ưu
- ❌ **Sai**: Intent sai hoàn toàn, hoặc chatbot không hiểu prompt
