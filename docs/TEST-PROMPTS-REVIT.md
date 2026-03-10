# Test Prompts for Revit Chatbot

> Open the chatbot in Revit, type each prompt in order within each Test Case.
> After each conversation chain, click **Clear Chat** then move to the next Test Case.
> Evaluation: Pass | Partial | Fail

---

## A. SINGLE PROMPT TESTS — Basic Recognition

Type each prompt individually (Clear Chat between prompts):

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
| 1 | `list ducts on level 1` (VN: `liet ke ong gio tang 1`) | Query | Ducts |
| 2 | `count pipes on level 2` (VN: `dem ong nuoc tang 2`) | Count | Pipes |
| 3 | `check duct velocity` (VN: `kiem tra van toc ong gio`) | Check | Ducts |
| 4 | `resize pipes to 150mm` (VN: `doi kich thuoc ong nuoc sang 150mm`) | Modify | Pipes |
| 5 | `export ducts to CSV` (VN: `xuat ong gio ra CSV`) | Export | Ducts |
| 6 | `color pipes by system` (VN: `to mau ong nuoc theo he thong`) | Visual | Pipes |
| 7 | `connect 2 selected ducts` (VN: `noi 2 ong gio dang chon`) | Connect | Ducts |
| 8 | `tag untagged pipes` (VN: `ghi chu ong nuoc chua tag`) | Tag | Pipes |
| 9 | `delete selected elements` (VN: `xoa phan tu dang chon`) | Delete | — |
| 10 | `what can you do` (VN: `ban co the lam gi`) | Help | — |

### A3. Numeric + Unit
| # | Prompt | Expected | Check |
|---|--------|----------|-------|
| 1 | `check velocity max 5 m/s` | Check, max_velocity=5 | Number + unit |
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
| 7 | `list supply air ducts` (VN: `liet ke ong he cap gio`) | Supply Air |
| 8 | `count fire protection pipes` (VN: `thong ke he PCCC`) | Fire Protection |

### A5. DryRun / Preview
| # | Prompt | Check |
|---|--------|-------|
| 1 | `preview resize ducts to 400mm` | DryRun = true |
| 2 | `preview resize ducts` (VN: `xem truoc doi size ong gio`) | DryRun = true |
| 3 | `simulate delete unused` | DryRun = true |
| 4 | `simulate changes` (VN: `mo phong thay doi`) | DryRun = true |

---

## B. CONVERSATION CHAIN TESTS — Context Carryover

> **IMPORTANT**: Type prompts sequentially WITHOUT clearing chat between steps.
> Purpose: verify the chatbot remembers context from previous turns.

### B1. Duct Workflow (EN) — 13 steps
```
Step 1:  list all ducts on level 1
         → Expect: Query Ducts on Level 1

Step 2:  how many are there
         → Expect: Count Ducts (carry from step 1, no need to say "ducts")

Step 3:  summarize by system
         → Expect: Analyze Ducts (carry category)

Step 4:  check velocity
         → Expect: Check Ducts velocity (carry category)

Step 5:  which ones exceed 5 m/s
         → Expect: Check + max_velocity=5

Step 6:  resize those to 400mm
         → Expect: Modify Ducts, diameter=400 (carry category)

Step 7:  preview first
         → Expect: DryRun=true, carry Modify Ducts

Step 8:  ok apply the changes
         → Expect: Modify Ducts (execute, not preview)

Step 9:  now check velocity again
         → Expect: Check Ducts (carry category)

Step 10: color them by system
         → Expect: Visual Ducts (carry category)

Step 11: export to CSV
         → Expect: Export Ducts (carry category)

Step 12: do the same for pipes
         → Expect: Export Pipes (switch category, carry intent)

Step 13: how many pipes total
         → Expect: Count Pipes
```

### B2. Pipe Workflow (VN) — 13 steps
```
Step 1:  list pipes on level 1
Step 2:  how many
Step 3:  summarize by system
Step 4:  check slope
Step 5:  set slope to 2%
Step 6:  preview changes
Step 7:  execute
Step 8:  re-check
Step 9:  color by system
Step 10: export to CSV
Step 11: same for ducts
Step 12: how many ducts
Step 13: summarize ducts
```

### B3. Multi-System Switch (EN) — 12 steps
```
Step 1:  list Supply Air ducts
Step 2:  count them
Step 3:  now show Exhaust Air ducts
Step 4:  how many
Step 5:  show Return Air ducts too
Step 6:  count those too
Step 7:  analyze Supply Air totals
Step 8:  same for Exhaust Air
Step 9:  and Return Air too
Step 10: check velocity for all ducts
Step 11: color all ducts by system
Step 12: export comparison to CSV
```

### B4. Mixed Language (EN↔VN) — 13 steps
```
Step 1:  list all ducts on level 1
         → EN: Query Ducts Level 1

Step 2:  how many (VN: dem bao nhieu)
         → VN: Count Ducts (carry from EN prompt)

Step 3:  summarize by system (VN: thong ke theo he thong)
         → VN: Analyze Ducts (carry)

Step 4:  check velocity
         → EN: Check Ducts (carry)

Step 5:  max velocity 5 m/s (VN: van toc toi da 5 m/s)
         → VN: Check, max=5

Step 6:  resize to 400mm
         → EN: Modify Ducts, 400mm

Step 7:  preview (VN: xem truoc)
         → VN: DryRun

Step 8:  ok do it
         → EN: Execute

Step 9:  re-check (VN: kiem tra lai)
         → VN: Check (carry Ducts)

Step 10: color by system
         → EN: Visual Ducts

Step 11: export CSV (VN: xuat CSV)
         → VN: Export Ducts

Step 12: also export IFC
         → EN: Export IFC

Step 13: thank you (VN: cam on ban)
         → VN: Help/Conversational
```

### B5. Level Switch — 12 steps
```
Step 1:  list all ducts on level 1
Step 2:  count them
Step 3:  check velocity
Step 4:  same thing on level 2
Step 5:  count those
Step 6:  check velocity like above
Step 7:  now level 3 ducts
Step 8:  count again
Step 9:  back to level 1 but pipes
Step 10: count them
Step 11: export level 1 data
Step 12: same but level 2 (VN: nhu tren nhung tang 2)
```

### B6. QA Audit Chain (Mixed) — 12 steps
```
Step 1:  check model health
Step 2:  check clashes (VN: kiem tra va cham)
Step 3:  check disconnected elements
Step 4:  check insulation (VN: kiem tra bao on)
Step 5:  how many warnings total
Step 6:  top 10 warnings
Step 7:  check velocity for ducts
Step 8:  check pipe slope (VN: kiem tra do doc ong nuoc)
Step 9:  fix the disconnected ducts
Step 10: re-check connections (VN: kiem tra lai ket noi)
Step 11: color problematic elements red
Step 12: export audit report to CSV
```

### B7. Connect & Route — 10 steps
```
Step 1:  connect 2 selected ducts (VN: noi 2 ong gio dang chon)
Step 2:  check connection (VN: kiem tra ket noi)
Step 3:  connect 2 selected pipes (VN: noi 2 ong nuoc dang chon)
Step 4:  check connection
Step 5:  route duct from FCU to main
Step 6:  avoid structural (VN: tranh ket cau)
Step 7:  check velocity after routing
Step 8:  connect pipe to equipment
Step 9:  tag new connections
Step 10: export to CSV
```

---

## C. SPECIAL TESTS

### C1. Ambiguous "pipe/duct" (VN context)
```
Step 1:  check supply air system pipes
         → Expect: Check Ducts (supply air = duct system)

Step 2:  list chilled water system pipes
         → Expect: Query Pipes (chilled water = pipe system)

Step 3:  count drainage system pipes
         → Expect: Count Pipes (drainage = sanitary pipe system)
```

### C2. Numeric Edge Cases
```
Step 1:  find pipes DN100
Step 2:  resize to DN150
Step 3:  find duct ø300
Step 4:  resize duct to 600x400mm
Step 5:  move up 500mm
```

### C3. DryRun → Execute Pattern
```
Step 1:  list ducts on level 1
Step 2:  preview resize to 400mm
Step 3:  try 500x300 instead
Step 4:  ok 500x300 is good, execute
Step 5:  check velocity
```

### C4. Self-Training Test
```
Step 1:  Click "Retrain" button (if available in UI)
         → Expect: Training report with:
           - Epoch results
           - Overall accuracy %
           - Conv accuracy % (conversation chains)
           - Examples + weights count

Step 2:  After training completes, try:
         list ducts on level 1
         → Compare speed and accuracy before/after training
```

---

## D. EVALUATION TABLE

After testing, fill in results:

| Test Case | qwen2.5:14b | qwen3:14b | Notes |
|-----------|:-----------:|:---------:|-------|
| A1. Intent EN | Pass/Partial/Fail | Pass/Partial/Fail | |
| A2. Intent VN | Pass/Partial/Fail | Pass/Partial/Fail | |
| A3. Numeric | Pass/Partial/Fail | Pass/Partial/Fail | |
| A4. System | Pass/Partial/Fail | Pass/Partial/Fail | |
| A5. DryRun | Pass/Partial/Fail | Pass/Partial/Fail | |
| B1. Duct EN | Pass/Partial/Fail | Pass/Partial/Fail | |
| B2. Pipe VN | Pass/Partial/Fail | Pass/Partial/Fail | |
| B3. Multi-System | Pass/Partial/Fail | Pass/Partial/Fail | |
| B4. Mixed Lang | Pass/Partial/Fail | Pass/Partial/Fail | |
| B5. Level Switch | Pass/Partial/Fail | Pass/Partial/Fail | |
| B6. QA Audit | Pass/Partial/Fail | Pass/Partial/Fail | |
| B7. Connect | Pass/Partial/Fail | Pass/Partial/Fail | |
| C1. Ambiguous | Pass/Partial/Fail | Pass/Partial/Fail | |
| C2. Numeric Edge | Pass/Partial/Fail | Pass/Partial/Fail | |
| C3. DryRun→Exec | Pass/Partial/Fail | Pass/Partial/Fail | |
| C4. Self-Train | Pass/Partial/Fail | Pass/Partial/Fail | |

### Evaluation Criteria
- **Pass**: Intent + Category + System + Tool all correct
- **Partial**: Intent correct but category/system wrong, or tool suboptimal
- **Fail**: Intent completely wrong, or chatbot does not understand the prompt

---

## E. RESPONSE TIME BENCHMARK

> Record response time displayed at the end of each response.
> Test each prompt once, record time (s). Clear chat between single tests.
> Recommended VRAM: >= 12GB for 14b models.

### E1. Single Prompt — Response Time (seconds)

| # | Prompt | qwen2.5:14b | qwen3:14b | Delta |
|---|--------|:-----------:|:---------:|:-----:|
| 1 | `list all ducts on level 1` | — | — | — |
| 2 | `how many pipes on level 2` | — | — | — |
| 3 | `list ducts on level 1` (VN) | — | — | — |
| 4 | `count pipes on level 2` (VN) | — | — | — |
| 5 | `check velocity max 5 m/s` | — | — | — |
| 6 | `resize ducts to 400mm` | — | — | — |
| 7 | `list SA ducts` | — | — | — |
| 8 | `preview resize ducts to 400mm` | — | — | — |
| 9 | `connect 2 selected ducts` | — | — | — |
| 10 | `export ducts to CSV` | — | — | — |

### E2. Conversation Chain — Time per Step (seconds)

> Use B1 (Duct Workflow EN) for benchmarking. Record time per step.

| Step | Prompt | qwen2.5:14b | qwen3:14b |
|:----:|--------|:-----------:|:---------:|
| 1 | `list all ducts on level 1` | — | — |
| 2 | `how many are there` | — | — |
| 3 | `summarize by system` | — | — |
| 4 | `check velocity` | — | — |
| 5 | `which ones exceed 5 m/s` | — | — |
| 6 | `resize those to 400mm` | — | — |
| 7 | `preview first` | — | — |
| 8 | `ok apply the changes` | — | — |
| 9 | `now check velocity again` | — | — |
| 10 | `color them by system` | — | — |
| 11 | `export to CSV` | — | — |
| 12 | `do the same for pipes` | — | — |
| 13 | `how many pipes total` | — | — |
| | **Total / Average** | **—** | **—** |

### E3. Time Summary

| Metric | qwen2.5:14b | qwen3:14b | Winner |
|--------|:-----------:|:---------:|:------:|
| Avg single prompt (s) | — | — | — |
| Avg conversation step (s) | — | — | — |
| Min response (s) | — | — | — |
| Max response (s) | — | — | — |
| Total B1 chain (s) | — | — | — |

### E4. Observations

| Criteria | qwen2.5:14b | qwen3:14b |
|----------|:-----------:|:---------:|
| Speed | — | — |
| Accuracy | — | — |
| Tool Calling | — | — |
| Context Carryover | — | — |
| Mixed Language | — | — |
| **Overall** | — | — |
