# Phương Án Tối Ưu Code Và Tổ Chức

## Hiện Trạng

```mermaid
graph TD
  OCS["OllamaChatService.cs\n2,199 lines\n47% static data"] --> SK["32 Skill Files\n~155 tools"]
  OCS --> VM["BaseChatViewModel.cs\n623 lines\n14 responsibilities"]
  SK --> RH["RevitHelpers.cs\n195 lines shared"]
  SK --> SR["SkillRegistry.cs\n121 lines"]

  subgraph dataInline [Hardcoded Data in OCS]
    FS["228 FewShot examples\n~553 lines"]
    KG["33 KeywordGroups\n~203 lines"]
    TSH["174 ToolSchemaHints\n~225 lines"]
  end
  OCS --> dataInline
```

**Vấn đề chính:**

- `OllamaChatService.cs`: 2,199 dòng, 47% là data cứng (FewShot, Keywords, Hints)
- 32 skills lặp boilerplate: null check, transaction, element resolution (58 transaction blocks)
- `BaseChatViewModel`: 623 dòng, 14 trách nhiệm (chat flow + tool exec + context + memory + UI + feedback + risk + cancel + settings + diagnostics)
- `TruncateToolResults` (lines 315–331) là dead code — `CompressAndTruncate` được dùng thay thế
- Mọi config tool đều hardcoded trong C# — thêm/sửa tool phải rebuild

---

## Phương Án A: Tách Static Data Ra JSON Config

**Scope:** OllamaChatService.cs only

**Làm gì:**

- Tách `FewShotExamples` (228 entries) ra `fewshot_examples.json`
- Tách `KeywordGroups` (33 groups) ra `keyword_groups.json`
- Tách `ToolSchemaHints` (174 entries) ra `tool_schema_hints.json`
- Tách `NormalizationMap`, `ActionKeywords`, `ChitchatPatterns` ra `chat_config.json`
- Load tất cả từ JSON khi khởi tạo service

**Ưu điểm:**

- OllamaChatService giảm ~1,038 dòng (47%) -> ~1,161 dòng logic thuần
- Thêm/sửa FewShot, Keywords không cần rebuild DLL
- Non-developer (Digital Lead) có thể edit JSON trực tiếp
- Có thể deploy JSON riêng, hot-reload khi cần
- Risk thấp — chỉ di chuyển data, logic không đổi

**Nhược điểm:**

- Cần validation khi load JSON (schema lỗi = crash)
- Mất compile-time type safety cho data
- Phải maintain JSON schema documentation
- Deployment phức tạp hơn chút (DLL + JSON files)

**Effort:** Thấp-Trung bình (~2-3 giờ)

---

## Phương Án B: Base Skill Class + Transaction Helper

**Scope:** 32 skill files

**Làm gì:**

- Tạo `BaseSkill` abstract class:
  - `Execute(string functionName, UIApplication app, Dictionary<string, object> args)` wrapper tự check `uidoc` null + route tool
  - `ExecuteInTransaction(doc, name, action)` helper tự try/catch/rollback
  - `ResolveElements(doc, args)` helper tự parse + validate element IDs
  - `GetRequiredArg<T>()` tự throw nếu missing
- Mỗi skill chỉ cần override `ExecuteTool(string tool, UIDocument uidoc, Document doc, Dictionary<string, object> args)`
- Loại bỏ boilerplate lặp trong 32 files

> **Lưu ý:** Signature thực tế dùng `UIApplication` + `Dictionary<string, object>`, KHÔNG phải `UIDocument` + `JsonElement`.
> `UIDocument` được lấy qua `app.ActiveUIDocument` bên trong `Execute()`.

**Before:**

```csharp
public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
{
    var uidoc = app.ActiveUIDocument;
    if (uidoc == null) return JsonError("No active document.");
    var doc = uidoc.Document;
    return functionName switch
    {
        "tool_a" => DoToolA(doc, args),
        _ => JsonError($"MySkill: unknown tool '{functionName}'")
    };
}

private string DoToolA(Document doc, Dictionary<string, object> args)
{
    using var trans = new Transaction(doc, "AI: Tool A");
    try
    {
        trans.Start();
        // ... logic ...
        trans.Commit();
        return result;
    }
    catch (Exception ex)
    {
        if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
        return JsonError($"Tool A failed: {ex.Message}");
    }
}
```

**After:**

```csharp
protected override string ExecuteTool(
    string tool, UIDocument uidoc, Document doc,
    Dictionary<string, object> args)
{
    return tool switch
    {
        "tool_a" => DoToolA(doc, args),
        _ => UnknownTool(tool)
    };
}

private string DoToolA(Document doc, Dictionary<string, object> args)
{
    return ExecuteInTransaction(doc, "Tool A", () =>
    {
        // ... logic only ...
        return result;
    });
}
```

**Ưu điểm:**

- Loại bỏ ~15-20 dòng boilerplate mỗi skill (x32 files = ~500-640 dòng)
- Transaction handling nhất quán — không sót rollback
- Null check không bao giờ quên
- Dễ thêm cross-cutting concerns (logging, timing, etc.)

**Nhược điểm:**

- Refactor 32 files = risk regression cao
- Cần test kỹ từng skill sau refactor
- Inheritance có thể phức tạp nếu skill cần custom behavior
- Không giảm được logic code — chỉ giảm boilerplate

**Effort:** Trung bình (~4-6 giờ)

---

## Phương Án C: Tách BaseChatViewModel

**Scope:** BaseChatViewModel.cs + liên quan

**Làm gì:**

- Extract `ToolExecutionService` — chứa `ExecuteToolCallsAsync`, timeout, `TaskCompletionSource`
- Extract `RiskAssessmentService` — chứa `IsRiskyToolCall`, `RequiresConfirmation`, hardcoded risky tools set
- Extract `ContextCollector` — chứa `CollectContextAsync`, keyword-based context selection
- Split `SendAsync` (~186 dòng, lines 102–287) thành:
  - `HandleConfirmationFlow()`
  - `ExecuteToolLoop()`
  - `ProcessToolResults()`

**Ưu điểm:**

- Single Responsibility — mỗi class 1 việc
- Dễ test từng service riêng
- `SendAsync` dễ đọc hơn
- Mở đường cho dependency injection

**Nhược điểm:**

- Tăng số file/class
- Cần wire dependencies (constructor injection)
- Risk regression trên flow chính (chat loop)
- Benefit nhỏ nếu không viết unit test

**Effort:** Trung bình (~3-4 giờ)

---

## Phương Án D: Full Refactor (A + B + C) — Chi Tiết

**Scope:** Toàn bộ RevitChat + RevitChatLocal

```mermaid
graph TD
  subgraph after [After Refactor]
    OCS2["OllamaChatService.cs\n~1,264 lines\nLogic only"]
    JSON["JSON Config Files\nFewShot + Keywords\n+ Hints + NormMap"]
    BS["BaseSkill\nNull check + Transaction\n+ Element resolution"]
    SK2["32 Skills\nLogic only\nNo boilerplate"]
    TES["ToolExecutionService"]
    RAS["ConfirmationPolicy"]
    CC["ContextCollector"]
    RC["ResultCompressor"]
    VM2["BaseChatViewModel\n~250 lines\nUI + chat flow only"]
  end

  JSON --> OCS2
  BS --> SK2
  TES --> VM2
  RAS --> VM2
  CC --> VM2
  RC --> VM2
```

---

### D1. Tách Static Data Ra JSON (= Phương Án A)

**Hiện trạng OllamaChatService.cs — 2,199 dòng:**

| Section | Line Range | Lines | Type |
|---------|-----------|-------|------|
| CoreTools (24 tool names) | 45–53 | 9 | Data |
| KeywordGroups (33 groups) | 56–258 | 203 | Data |
| NormalizationMap (25 entries) | 260–286 | 27 | Data |
| ActionKeywords (75 entries) | 288–300 | 13 | Data |
| ToolSchemaHints (174 entries) | 302–526 | 225 | Data |
| FewShotExamples (228 entries) | 532–1084 | 553 | Data |
| ChitchatPatterns (21 entries) | 1251–1258 | 8 | Data |
| **Tổng data** | | **~1,038** | **47.2%** |
| **Logic (methods)** | | **~1,161** | **52.8%** |

**Tách ra 4 JSON files:**

```
HD.extension/lib/net8/Data/ChatConfig/
├── fewshot_examples.json      (228 entries)
├── keyword_groups.json        (33 groups + CoreTools + ActionKeywords)
├── tool_schema_hints.json     (174 entries)
└── chat_normalization.json    (NormMap + ChitchatPatterns)
```

**Schema ví dụ `fewshot_examples.json`:**
```json
[
  {
    "keywords": ["list", "get", "show", "liệt kê", "xem"],
    "example": "User: liệt kê tất cả phòng trên tầng 1\nAssistant:\n<tool_call>\n{\"name\": \"get_elements\", \"arguments\": {\"category\": \"Rooms\", \"level\": \"Level 1\"}}\n</tool_call>"
  }
]
```

**Schema ví dụ `keyword_groups.json`:**
```json
{
  "core_tools": ["get_elements", "count_elements", "..."],
  "action_keywords": ["count", "how many", "list", "..."],
  "groups": [
    {
      "name": "Color / Override",
      "keywords": ["color", "red", "blue", "override"],
      "tools": ["override_element_color", "override_category_color"],
      "weight": 1
    }
  ]
}
```

**Loading code (thêm vào constructor):**
```csharp
private static readonly string _dataDir = Path.Combine(
    Path.GetDirectoryName(typeof(OllamaChatService).Assembly.Location)!,
    "Data", "ChatConfig");

private static T LoadJson<T>(string fileName)
{
    var path = Path.Combine(_dataDir, fileName);
    if (!File.Exists(path))
        throw new FileNotFoundException($"Chat config not found: {path}");
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidOperationException($"Failed to parse: {path}");
}
```

**Kết quả:** OllamaChatService giảm từ 2,199 → ~1,161 dòng.

---

### D2. BaseSkill + Transaction Helper (= Phương Án B)

**Hiện trạng: Boilerplate lặp trong 32 skills**

Đoạn code sau xuất hiện trong **32/32 skill files**:
```csharp
public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
{
    var uidoc = app.ActiveUIDocument;
    if (uidoc == null) return JsonError("No active document.");
    var doc = uidoc.Document;
    return functionName switch
    {
        "tool_a" => DoToolA(doc, args),
        _ => JsonError($"XxxSkill: unknown tool '{functionName}'")
    };
}
```

Transaction pattern xuất hiện **58 lần** qua 32 files:
```csharp
using (var trans = new Transaction(doc, "AI: ..."))
{
    try
    {
        trans.Start();
        // logic
        trans.Commit();
        return result;
    }
    catch (Exception ex)
    {
        if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
        return JsonError($"... failed: {ex.Message}");
    }
}
```

**Tạo BaseSkill.cs (mới):**

```csharp
public abstract class BaseSkill : IRevitSkill
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<ChatTool> GetToolDefinitions();

    protected abstract HashSet<string> HandledTools { get; }
    public bool CanHandle(string fn) => HandledTools.Contains(fn);

    // Template method — null check + routing
    public string Execute(string functionName, UIApplication app,
                          Dictionary<string, object> args)
    {
        var uidoc = app?.ActiveUIDocument;
        if (uidoc == null) return JsonError("No active document.");
        return ExecuteTool(functionName, uidoc, uidoc.Document, args);
    }

    protected abstract string ExecuteTool(
        string tool, UIDocument uidoc, Document doc,
        Dictionary<string, object> args);

    protected string UnknownTool(string tool)
        => JsonError($"{Name}: unknown tool '{tool}'");

    // Transaction helper — try/catch/rollback built-in
    protected string ExecuteInTransaction(
        Document doc, string name, Func<string> action)
    {
        using var trans = new Transaction(doc, $"AI: {name}");
        try
        {
            trans.Start();
            var result = action();
            trans.Commit();
            return result;
        }
        catch (Exception ex)
        {
            if (trans.GetStatus() == TransactionStatus.Started)
                trans.RollBack();
            return JsonError($"{name} failed: {ex.Message}");
        }
    }

    // View-aware variant
    protected string ExecuteWithView(
        Document doc, Func<View, string> action)
    {
        var view = doc.ActiveView;
        if (view == null) return JsonError("No active view.");
        return action(view);
    }
}
```

**Skill refactored (ví dụ GroupSkill):**

Before (309 lines) → After (~280 lines, giảm ~30 lines):

```csharp
public class GroupSkill : BaseSkill
{
    public override string Name => "Group";
    public override string Description => "Group operations";
    protected override HashSet<string> HandledTools { get; } = new()
    {
        "get_groups", "create_group", "ungroup",
        "get_group_members", "place_group_instance"
    };

    protected override string ExecuteTool(
        string tool, UIDocument uidoc, Document doc,
        Dictionary<string, object> args)
    {
        return tool switch
        {
            "get_groups"          => GetGroups(doc, args),
            "create_group"        => CreateGroup(doc, uidoc, args),
            "ungroup"             => Ungroup(doc, args),
            "get_group_members"   => GetGroupMembers(doc, args),
            "place_group_instance"=> PlaceGroupInstance(doc, args),
            _ => UnknownTool(tool)
        };
    }

    private string CreateGroup(Document doc, UIDocument uidoc,
                               Dictionary<string, object> args)
    {
        return ExecuteInTransaction(doc, "Create Group", () =>
        {
            // logic only — no try/catch/rollback needed
            var ids = GetArgLongArray(args, "element_ids");
            // ...
            return JsonSerializer.Serialize(new { ... });
        });
    }
}
```

**Impact trên 32 files:**
- 58 transaction blocks giảm 8-10 dòng mỗi block → tiết kiệm ~464-580 dòng
- 32 Execute() wrappers giảm 5-7 dòng mỗi file → tiết kiệm ~160-224 dòng
- **Tổng giảm: ~624-804 dòng** qua 32 files

**Thứ tự refactor (theo risk, dựa trên số transaction thực tế):**
1. Skills không có transaction (0 risk) — **16 files:**
   ClashDetectionSkill, CoordinationReportSkill, ExportSkill, MepEquipmentSkill,
   MepQuantityTakeoffSkill, MepSpaceSkill, MepSystemAnalysisSkill, MepValidationSkill,
   ModelHealthSkill, NamingAuditSkill, ProjectInfoSkill, PurgeAuditSkill,
   QuerySkill, RoomAreaSkill, SelectionFilterSkill, RevitLinkSkill
2. Skills có 1-3 transactions (low risk) — **12 files:**
   DimensionTagSkill(3), FamilyPlacementSkill(2), FilterTemplateSkill(2),
   GroupSkill(3), MaterialSkill(1), MepConnectivitySkill(1),
   RevisionMarkupSkill(1), ScheduleSkill(2), SharedParameterSkill(1),
   SheetManagementSkill(3), WorksetPhaseSkill(2)
3. Skills có 5+ transactions (medium-high risk) — **4 files:**
   MepModelerSkill(5), GridLevelSkill(5), ModifySkill(8), ViewControlSkill(18)

---

### D3. Tách BaseChatViewModel (= Phương Án C)

**Hiện trạng: BaseChatViewModel.cs — 623 dòng, 14 trách nhiệm:**

```mermaid
graph LR
  subgraph current [BaseChatViewModel — 623 lines]
    SF["SendAsync\n186 lines (102–287)"]
    TE["ToolExecution\n22 lines (292–313)"]
    CTX["ContextCollect\n38 lines (487–524)"]
    RISK["Risk/Confirm\n44 lines (403–447)"]
    MEM["Memory/Compress\n19 lines (467–485)"]
    FB["Feedback\n40 lines (557–597)"]
    UI["UI State\n30 lines"]
    CMD["Commands\n30 lines"]
    DEAD["TruncateToolResults\n17 lines (315–331)\nDEAD CODE"]
  end
```

> **Lưu ý:** `TruncateToolResults` (lines 315–331) được định nghĩa nhưng không bao giờ được gọi.
> `CompressAndTruncate` (lines 467–485) được dùng thay thế. Nên xóa dead code này.

**Tách thành:**

```mermaid
graph TD
  VM["BaseChatViewModel\n~250 lines\nUI + SendAsync orchestration"]
  VM --> ITE["IToolExecutionService\n~80 lines"]
  VM --> ICP["IConfirmationPolicy\n~50 lines"]
  VM --> ICC["IContextCollector\n~60 lines"]
  VM --> IRC["IResultCompressor\n~50 lines"]
  
  ITE --> EE["ExternalEvent"]
  ITE --> Q["ChatRequestQueue"]
  ITE --> H["RevitChatHandler"]
  
  ICC --> ITE
```

**File mới 1: `Services/ToolExecutionService.cs` (~80 dòng)**
```csharp
public interface IToolExecutionService
{
    Task<Dictionary<string, string>> ExecuteAsync(
        List<ToolCallRequest> calls, int timeoutMs, CancellationToken ct);
}

public class ToolExecutionService : IToolExecutionService
{
    // Di chuyển từ BaseChatViewModel:
    // - ExecuteToolCallsAsync (lines 292–313)
    // - HandleToolCallsCompleted (lines 334–337)
    // - HandleHandlerError (lines 339–342)
    // - InvokeOnDispatcher (lines 343–352)
}
```

**File mới 2: `Services/ConfirmationPolicy.cs` (~50 dòng)**
```csharp
public interface IConfirmationPolicy
{
    bool RequiresConfirmation(List<ToolCallRequest> calls, out string prompt);
    bool IsConfirmMessage(string text);
    bool IsCancelMessage(string text);
}

public class ConfirmationPolicy : IConfirmationPolicy
{
    // Di chuyển từ BaseChatViewModel:
    // - RequiresConfirmation (lines 403–414)
    // - IsRiskyToolCall (lines 416–447) — risky tools set configurable
    // - IsConfirmMessage (lines 383–390)
    // - IsCancelMessage (lines 392–398)
}
```

**File mới 3: `Services/ContextCollector.cs` (~60 dòng)**
```csharp
public interface IContextCollector
{
    Task<string> CollectContextAsync(
        string userText, IToolExecutionService executor,
        int timeoutMs, CancellationToken ct);
}

public class ContextCollector : IContextCollector
{
    // Di chuyển từ BaseChatViewModel:
    // - CollectContextAsync (lines 487–524)
    // - Keyword → tool mapping configurable
}
```

**File mới 4: `Services/ResultCompressor.cs` (~50 dòng)**
```csharp
public interface IResultCompressor
{
    Dictionary<string, string> CompressAndTruncate(
        Dictionary<string, string> results,
        List<ToolCallRequest> calls,
        WorkingMemory memory, int maxChars);
}

public class ResultCompressor : IResultCompressor
{
    // Di chuyển từ BaseChatViewModel:
    // - CompressAndTruncate (lines 467–485)
    // - TruncateToolResults (lines 315–331) — dead code, cần xóa hoặc tích hợp
    // - GetTotalChars (lines 424–431)
}
```

**SendAsync sau refactor (~100 dòng thay vì 186):**
```csharp
protected async Task SendAsync(string text)
{
    // 1. Input validation (10 lines)
    // 2. Confirmation/Cancel branch → _confirmationPolicy (15 lines)
    // 3. Context → _contextCollector.CollectContextAsync (5 lines)
    // 4. Send → ChatService.SendMessageAsync (3 lines)
    // 5. Tool loop: execute → compress → continue (30 lines)
    // 6. Final response (10 lines)
    // 7. Exception handling (15 lines)
}
```

---

### D4. Execution Plan — Thứ Tự Thực Hiện

```mermaid
gantt
    title Plan D Execution Order
    dateFormat X
    axisFormat %s

    section Phase1_JSON
    Create JSON files            :a1, 0, 2
    Add loader + validation      :a2, 2, 3
    Update OllamaChatService     :a3, 3, 5
    Test keyword matching        :a4, 5, 6

    section Phase2_BaseSkill
    Create BaseSkill class       :b1, 6, 7
    Refactor no-transaction skills :b2, 7, 8
    Refactor low-transaction skills :b3, 8, 10
    Refactor high-transaction skills :b4, 10, 11

    section Phase3_ViewModel
    Extract ToolExecutionService :c1, 11, 12
    Extract ConfirmationPolicy   :c2, 12, 13
    Extract ContextCollector     :c3, 13, 14
    Extract ResultCompressor     :c4, 14, 15
    Refactor SendAsync           :c5, 15, 16
```

| Phase | Effort | Files Changed | Risk | Checkpoint |
|-------|--------|---------------|------|------------|
| D1. JSON Config | 2-3h | 1 file + 4 JSON mới | Thấp | Build + test keyword matching |
| D2. BaseSkill | 4-6h | 33 files (1 mới + 32 sửa) | Trung bình | Build + test từng nhóm skill |
| D3. ViewModel | 3-4h | 1 file + 4 files mới | Trung bình | Build + test chat flow |
| **Tổng** | **9-13h** | **38 files** | | |

---

### D5. Kết Quả Dự Kiến

| Metric | Before | After | Change |
|--------|--------|-------|--------|
| OllamaChatService.cs | 2,199 lines | ~1,161 lines | **-47%** |
| BaseChatViewModel.cs | 623 lines | ~250 lines | **-60%** |
| Total skill boilerplate | ~804 lines | 0 | **-100%** |
| Dead code removed | TruncateToolResults (17 lines) | 0 | clean |
| Files changed/created | — | 38 changed, 9 created | — |
| External config files | 1 (ollama_config) | 5 | +4 |
| Largest file | 2,199 lines | ~1,161 lines | — |

**Ưu điểm:**

- Codebase sạch nhất, dễ maintain nhất
- Mỗi component có trách nhiệm rõ ràng
- Non-dev có thể edit FewShot/Keywords qua JSON
- Transaction handling nhất quán — không sót rollback
- Dễ onboard developer mới
- Mỗi phase test riêng — rollback 1 phase không ảnh hưởng phase khác

**Nhược điểm:**

- Effort lớn nhất (9-13h)
- Risk regression — đụng 38 files
- Cần Revit để test toàn bộ sau mỗi phase
- JSON validation phải robust (file missing/corrupt = crash)
- Tăng 9 files mới → repo phức tạp hơn

**Effort:** Cao (~9-13 giờ), chia 3 phases độc lập

---

## So Sánh Tổng Quát

| Tiêu chí | A (JSON) | B (BaseSkill) | C (ViewModel) | D (Full) |
|----------|----------|---------------|----------------|----------|
| Impact | OllamaChatService -47% | 32 skills -boilerplate (58 trans) | ViewModel 623→250 lines | All |
| Risk | Thấp | Trung bình | Trung bình | Cao |
| Effort | 2-3h | 4-6h | 3-4h | 9-13h |
| ROI ngay | Cao (edit JSON) | Trung bình | Thấp | Cao (long-term) |
| Priority | **1st** | **2nd** | 3rd | Combo |

**Đề xuất:** Bắt đầu với **A** (risk thấp, ROI cao nhất), sau đó **B** nếu cần.

---

## Phương Án E: Tối Ưu Tốc Độ (Speed)

**Phân tích bottleneck hiện tại:**

```mermaid
graph LR
  subgraph timeline [User nhấn Send → Nhận response]
    CTX["Context Collection\nRevit thread\n~0.5-2s"] --> LLM1["LLM Call 1\nOllama HTTP\n~2-10s\nBLOCKING"]
    LLM1 --> TOOL["Tool Execution\nRevit thread\n~0.5-5s"]
    TOOL --> LLM2["LLM Call 2\nOllama HTTP\n~2-8s\nBLOCKING"]
  end
```

**Thời gian chờ trung bình: 5-25 giây, user không thấy gì cho tới khi xong.**

### E1. Streaming LLM Response (Impact cao nhất)

**Hiện trạng:** `CompleteChatAsync` chờ full response → user nhìn "Thinking..." 2-10 giây.

**Cải thiện:** Dùng `CompleteChatStreamingAsync` → token xuất hiện ngay lập tức.

```csharp
// BEFORE: blocking
var response = await _client.CompleteChatAsync(messages, options, ct);
var text = response.Value.Content?.FirstOrDefault()?.Text ?? "";

// AFTER: streaming
var sb = new StringBuilder();
await foreach (var update in _client.CompleteChatStreamingAsync(messages, options, ct))
{
    var token = update.ContentUpdate?.FirstOrDefault()?.Text;
    if (token != null)
    {
        sb.Append(token);
        OnTokenReceived?.Invoke(token); // UI cập nhật realtime
    }
}
var text = sb.ToString();
```

- **Perceived latency:** Giảm từ ~5s → ~0.3s (first token)
- **Effort:** 3-4h (cần sửa OllamaChatService + ViewModel UI binding)
- **Risk:** Trung bình — phải handle streaming tool_call detection
- **Lưu ý:** Cần detect `<tool_call>` tag giữa stream → buffer cho đến khi `</tool_call>` hoặc text thuần

### E2. Giảm Tool Catalog Size

**Hiện trạng:** Smart mode gửi 30-80 tools (2-6 KB) → LLM xử lý chậm hơn với context lớn.

**Cải thiện:** Giới hạn 15-20 tools liên quan nhất:
- CoreTools (24) → giảm xuống 15 tools thiết yếu nhất
- Keyword-matched tools: chỉ lấy top 2 KeywordGroups (thay vì tất cả matched)
- Ưu tiên tools có FewShot example score > 0

**Impact:** Giảm ~40-60% tokens trong system prompt → LLM inference nhanh hơn ~20-30%.

### E3. Token-Aware History Trimming

**Hiện trạng:** Giữ tối đa 40 messages, trim bằng cách tóm tắt thô. Tool results có thể 4000 chars/result.

**Cải thiện:**
- Đặt token budget (~4000 tokens cho history)
- Ước lượng tokens: `chars / 3.5` (xấp xỉ)
- Trim từ oldest, giữ 2 turn gần nhất nguyên vẹn
- Tóm tắt cũ thành 1-2 dòng thay vì giữ nguyên

**Impact:** LLM inference nhanh hơn cho conversation dài; giảm risk context overflow.

### E4. Cache Few-Shot Scoring

**Hiện trạng:** `BuildDynamicExamples` duyệt 228 entries + gọi `GetSimilarApproved` (file I/O) mỗi lần gửi message.

**Cải thiện:** Cache kết quả scoring theo normalized message hash. Clear cache khi FewShot data thay đổi.

**Impact:** Giảm ~50-100ms mỗi request (nhỏ, nhưng cộng dồn).

---

## Phương Án F: Tối Ưu Độ Chính Xác (Accuracy)

**Phân tích vấn đề hiện tại:**

| Vấn đề | Hậu quả | Tần suất |
|--------|---------|----------|
| Keyword scoring chỉ đếm match | Miss relevant FewShot examples | Thường xuyên |
| Không validate required params | Tool fail ở execution, user thấy error | Trung bình |
| Fuzzy match dùng substring | Chọn sai tool (get_elements vs get_element_parameters) | Thỉnh thoảng |
| Malformed JSON → silent fail | User không nhận được response | Hiếm |
| Không có disambiguation rule rõ ràng | LLM chọn tool ngẫu nhiên khi nhiều tool phù hợp | Trung bình |

### F1. Required Parameter Validation + Retry

**Hiện trạng:** LLM output thiếu required params → skill trả về `JsonError("category required")` → user thấy lỗi.

**Cải thiện:** Validate trước khi gọi skill:
```csharp
private (bool valid, string error) ValidateToolCall(ToolCallRequest call)
{
    if (!ToolSchemaHints.TryGetValue(call.FunctionName, out var hint))
        return (true, null); // no hint = skip validation

    // Parse required params from hint (those without '?')
    var required = ParseRequiredParams(hint);
    foreach (var param in required)
    {
        if (!call.Arguments.ContainsKey(param))
            return (false, $"Missing required parameter: {param}");
    }
    return (true, null);
}
```

Nếu thiếu param → gửi lại LLM với error message cụ thể → LLM tự sửa.

**Impact:** Giảm ~80% lỗi thiếu param.

### F2. Cải Thiện Fuzzy Match (Levenshtein)

**Hiện trạng:** Substring match: `tool.Contains(lower)` → "get_elements" match "get_element_parameters".

**Cải thiện:** Dùng Levenshtein distance + prefix priority:
```csharp
private string FuzzyMatchToolName(string candidate)
{
    // 1. Exact match
    // 2. Feedback correction
    // 3. Levenshtein distance <= 3 (nhỏ nhất thắng)
    // 4. Part-based scoring (existing)
}
```

**Impact:** Giảm ~90% false match.

### F3. Retry On Malformed JSON

**Hiện trạng:** `ExtractToolCalls` thất bại → trả về empty list → user thấy "Done" nhưng không có kết quả.

**Cải thiện:** Khi parse fail, gửi lại LLM với guidance:
```
Your previous response was not valid. Please output a valid tool call:
<tool_call>
{"name": "tool_name", "arguments": {...}}
</tool_call>
```

Retry tối đa 1 lần. Nếu vẫn fail → trả fallback message.

**Impact:** Giảm ~70% silent failures.

### F4. Weighted Keyword Scoring

**Hiện trạng:** Score = count of matched keywords. "duct" và "summary" mỗi cái đều +1 → không phân biệt quan trọng.

**Cải thiện:** Thêm weight cho keywords:
- Category keywords (duct, pipe, wall): weight 2
- Action keywords (summary, count, create): weight 3
- Generic keywords (get, show, list): weight 1

**Impact:** FewShot selection chính xác hơn ~30%.

### F5. System Prompt — Disambiguation Rule

**Hiện trạng:** Không có rule rõ ràng khi nhiều tools phù hợp.

**Cải thiện:** Thêm rule:
```
12. When multiple tools could match the request:
    - Prefer tools that EXACTLY match the category mentioned
    - Prefer specific tools over generic ones (get_duct_summary > get_elements)
    - If still ambiguous, ask the user to clarify
```

**Impact:** Giảm chọn nhầm tool ~40%.

---

## Phương Án D+ (Full): Maintainability + Speed + Accuracy

Kết hợp tất cả:

```mermaid
graph TD
  subgraph phase1 [Phase 1: JSON Config — 2-3h]
    D1["Tách static data ra JSON"]
  end

  subgraph phase2 [Phase 2: BaseSkill — 4-6h]
    D2["BaseSkill abstract class\n+ Transaction helper"]
  end

  subgraph phase3 [Phase 3: ViewModel — 3-4h]
    D3["Tách services\nTool/Risk/Context/Compress"]
  end

  subgraph phase4 [Phase 4: Speed — 4-5h]
    E1["Streaming LLM response"]
    E2["Giảm tool catalog"]
    E3["Token-aware trimming"]
  end

  subgraph phase5 [Phase 5: Accuracy — 3-4h]
    F1["Param validation + retry"]
    F3["Retry on malformed JSON"]
    F5["Disambiguation rule"]
    F2["Levenshtein fuzzy match"]
    F4["Weighted keyword scoring"]
  end

  phase1 --> phase2
  phase2 --> phase3
  phase1 --> phase4
  phase1 --> phase5
```

### So Sánh Tổng Quát

| Tiêu chí | D (maintain) | E (speed) | F (accuracy) | D+ (tất cả) |
|----------|-------------|-----------|--------------|-------------|
| Tốc độ response | Không đổi | **Nhanh hơn 50-70%** | Không đổi | **Nhanh hơn 50-70%** |
| Độ chính xác | Không đổi | Không đổi | **Tăng ~40-60%** | **Tăng ~40-60%** |
| Maintainability | **Tốt hơn nhiều** | Không đổi | Tốt hơn chút | **Tốt hơn nhiều** |
| Effort | 9-13h | 6-8h | 5-7h | **16-24h** |
| Risk | Trung bình | Trung bình-Cao | Thấp-Trung bình | Cao |

### Thứ Tự Triển Khai Khuyến Nghị

| Priority | Phase | Impact | Effort | Có thể test riêng |
|----------|-------|--------|--------|--------------------|
| 1 | **E1: Streaming** | UX tốt nhất | 3-4h | Co |
| 2 | **F1+F3: Validation + Retry** | Giảm lỗi nhiều nhất | 2-3h | Co |
| 3 | **D1: JSON Config** | Dễ maintain nhất | 2-3h | Co |
| 4 | **F5: Disambiguation Rule** | Chính xác hơn | 0.5h | Co |
| 5 | **E2+E3: Catalog + Trimming** | Nhanh hơn | 2-3h | Co |
| 6 | **D2: BaseSkill** | Clean code | 4-6h | Co |
| 7 | **F2+F4: Fuzzy + Weighted** | Chính xác thêm | 2-3h | Co |
| 8 | **D3: ViewModel** | Clean code | 3-4h | Co |
