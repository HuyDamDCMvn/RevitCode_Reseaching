# HD AI Connector — MCP Architecture Plan

> Tài liệu tổng hợp nghiên cứu, kiến trúc và kế hoạch triển khai MCP Server cho Revit.
> Ngày tạo: 2026-02-22

---

## Mục lục

1. [Nghiên cứu NonicaTab AI Connector](#1-nghiên-cứu-nonicatab-ai-connector)
2. [Phân tích codebase hiện tại](#2-phân-tích-codebase-hiện-tại)
3. [So sánh với NonicaTab](#3-so-sánh-với-nonicatab)
4. [Tham khảo revit-mcp-plugin (open source)](#4-tham-khảo-revit-mcp-plugin-open-source)
5. [Kiến trúc mục tiêu](#5-kiến-trúc-mục-tiêu)
6. [Chi tiết kỹ thuật MCP Server bằng C#](#6-chi-tiết-kỹ-thuật-mcp-server-bằng-c)
7. [Kế hoạch triển khai 6 Phase](#7-kế-hoạch-triển-khai-6-phase)
8. [Files cần tạo và sửa](#8-files-cần-tạo-và-sửa)
9. [Lưu ý kỹ thuật](#9-lưu-ý-kỹ-thuật)
10. [Tài nguyên cần chuẩn bị](#10-tài-nguyên-cần-chuẩn-bị)

---

## 1. Nghiên cứu NonicaTab AI Connector

### Nguồn gốc

Video trên kênh BIM Pure (12/01/2026) giới thiệu **NonicaTab** — tiện ích mở rộng cho Revit kết nối với Claude Desktop qua Model Context Protocol (MCP).

### Cách hoạt động

- NonicaTab cung cấp 50+ tools chuyên dụng cho AI đọc/ghi dữ liệu mô hình Revit
- AI gọi tools qua MCP (không tự sinh mã Revit)
- Dữ liệu xử lý cục bộ, truyền tới AI theo chính sách quyền riêng tư
- Yêu cầu: Revit 2022+, cài NonicaTab + AI Connector, mở Claude Desktop

### Tính năng chính

| Tính năng | Mô tả |
|-----------|-------|
| Báo cáo toàn diện | Thống kê elements, types, parameters, dự án |
| Phân tích hiệu năng | Kiểm tra cảnh báo, xác định nút thắt |
| Kiểm tra tiếp cận | Đánh giá ADA compliance |
| Trích xuất schedules | Tạo biểu đồ, dashboard |
| Kiểm soát chất lượng | Chỉnh sửa tham số, định dạng sai |
| Tạo views/sheets | Room elevations, bố trí lên sheet |
| Multi-agent | Nhiều AI song song trên tác vụ khác nhau |

### Kiến trúc NonicaTab

```
Claude Desktop ←(MCP/stdio)→ NonicaTab MCP Server ←→ Revit Add-in
```

- MCP Server chạy cục bộ, expose tools qua JSON-RPC
- AI client (Claude, Copilot, Cursor) kết nối qua MCP protocol
- Phiên bản miễn phí: ~37 tools (đọc/phân tích)
- Phiên bản Pro: 50+ tools (đọc + chỉnh sửa)

---

## 2. Phân tích codebase hiện tại

### Projects

| Project | Target | Mục đích |
|---------|--------|----------|
| HD.Core | net8.0-windows | Shared library |
| RevitChat | net8.0-windows | AI chatbot (OpenAI API) |
| RevitChatLocal | net8.0-windows | AI chatbot (Ollama local) |
| CommonFeature | net8.0-windows | Modeless tool |
| SmartTag | net8.0-windows | Smart tagging |
| CheckCode | net8.0-windows | Model checks |

### Skills & Tools (27 skills, 80+ tools)

**Skill Packs:**

| Pack | Skills |
|------|--------|
| Core | Query, ProjectInfo, Modify, Export |
| ViewControl | ViewControl, SelectionFilter |
| MEP | MepSystemAnalysis, MepEquipment, MepSpace, MepQuantityTakeoff, MepValidation, MepConnectivity, MepModeler |
| Modeler | FamilyPlacement, SheetManagement, FilterTemplate, DimensionTag, WorksetPhase, Group, Material, RoomArea, GridLevel, SharedParameter, RevisionMarkup, Schedule |
| BIMCoordinator | ModelHealth, NamingAudit, PurgeAudit, CoordinationReport, ClashDetection |
| LinkedModels | RevitLink |

### Kiến trúc AI Chat hiện tại

```
User message → IChatService.SendMessageAsync() (OpenAI/Ollama)
    → LLM returns tool calls
    → ViewModel enqueues ToolCallRequest → ChatRequestQueue
    → ExternalEvent.Raise()
    → RevitChatHandler.Execute() (Revit main thread)
    → SkillRegistry.ExecuteTool() → IRevitSkill.Execute()
    → JSON result → ContinueWithToolResultsAsync()
    → Loop until final answer
```

### Key interfaces

```csharp
public interface IRevitSkill
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ChatTool> GetToolDefinitions();
    bool CanHandle(string functionName);
    string Execute(string functionName, UIApplication app, Dictionary<string, object> args);
}
```

```csharp
public class SkillRegistry
{
    public void Register(IRevitSkill skill);
    public IReadOnlyList<ChatTool> GetAllToolDefinitions();
    public string ExecuteTool(string functionName, UIApplication app, Dictionary<string, object> args);
    public static SkillRegistry CreateDefault(); // registers all 27 skills
}
```

---

## 3. So sánh với NonicaTab

| Tính năng | NonicaTab | Codebase hiện tại | Ghi chú |
|-----------|-----------|-------------------|---------|
| Số lượng tools | ~50 (Pro) | **80+ tools** | Vượt trội |
| Đọc/phân tích | Có | **QuerySkill, ProjectInfoSkill, ModelHealthSkill** | Đầy đủ |
| Chỉnh sửa | Pro only | **ModifySkill** (set/delete/rename/copy/move/mirror) | Đầy đủ |
| MEP tools | Không rõ | **7 MEP skills** | Vượt trội |
| BIM Coordination | Có | **ClashDetection, CoordinationReport, NamingAudit, PurgeAudit** | Đầy đủ |
| View/Sheet | Có | **ViewControlSkill, SheetManagementSkill** | Đầy đủ |
| Giao thức AI | **MCP** | **OpenAI Function Calling** | Khác biệt lớn |
| AI backend | Claude Desktop (external) | **OpenAI API + Ollama local** | Cả cloud + local |
| UI chat | External app | **WPF modeless window** | UX tốt hơn |
| Multi-agent | Có | Chưa có | Gap |
| Tiếng Việt | Không | **Có** | Lợi thế |
| Feedback/Learning | Không rõ | **ChatFeedbackService** | Đã có |

**Kết luận**: Đã có ~80% so với NonicaTab. Phần thiếu chính: **MCP protocol layer**.

---

## 4. Tham khảo revit-mcp-plugin (open source)

**Repo**: https://github.com/mcp-servers-for-revit/revit-mcp-plugin

### Kiến trúc 3 phần

```
Claude Desktop ←(stdio)→ revit-mcp (TypeScript/Node.js) ←(TCP:8080)→ revit-mcp-plugin (C#)
```

1. **revit-mcp** (TypeScript): MCP Server, stdio transport, tool definitions hardcoded
2. **revit-mcp-plugin** (C#): Revit add-in, TcpListener:8080, JSON-RPC, ExternalEvent
3. **revit-mcp-commandset** (C#): External DLLs loaded at runtime

### Bài học rút ra

| Bài học | Chi tiết | Áp dụng |
|---------|---------|---------|
| TCP Socket hoạt động tốt | Đơn giản, debug dễ, mở rộng remote được | Dùng TCP thay Named Pipe |
| Tool definitions bị duplicate | TypeScript define schema, C# define logic → 2 nơi | Tránh: dùng dynamic discovery |
| Connection per request | Mỗi tool call tạo connection mới | Cải thiện: persistent connection |
| JSON-RPC chuẩn tốt | Cả MCP lẫn IPC đều dùng JSON-RPC | Dùng JSON-RPC cho IPC |
| 15 tools, không có MEP | Scope nhỏ hơn nhiều | Bạn có 80+ tools |
| TypeScript MCP server | MCP SDK TypeScript ra trước | Dùng C# SDK (đã có) |

---

## 5. Kiến trúc mục tiêu

```
╔══════════════════════════════════════════════════════════════════╗
║                     AI CLIENTS (Frontends)                       ║
╠══════════════════════════════════════════════════════════════════╣
║                                                                  ║
║  ┌─────────────────┐  ┌──────────────┐  ┌───────────────────┐   ║
║  │ Claude Desktop   │  │   Cursor     │  │ WPF Chat Window   │   ║
║  │ (MCP stdio)      │  │ (MCP stdio)  │  │ (OpenAI/Ollama)   │   ║
║  └────────┬─────────┘  └──────┬───────┘  └────────┬──────────┘   ║
║           │                   │                    │              ║
║           ▼                   ▼                    │              ║
║  ┌─────────────────────────────────────┐           │              ║
║  │     RevitMcp.exe (MCP Server)       │           │              ║
║  │     C# Console App                  │           │              ║
║  │     RevitProxyTool[] (dynamic)      │           │              ║
║  └────────────────┬────────────────────┘           │              ║
║                   │ TCP:8400                       │              ║
╠═══════════════════╪════════════════════════════════╪══════════════╣
║                   │        REVIT PROCESS           │              ║
║                   ▼                                ▼              ║
║  ┌─────────────────────┐          ┌─────────────────────────┐    ║
║  │  McpBridgeService   │          │  RevitChatHandler       │    ║
║  │  (TcpListener)      │          │  (ExternalEvent)        │    ║
║  │  RevitMcpHandler    │          │  ChatRequestQueue       │    ║
║  └─────────┬───────────┘          └────────────┬────────────┘    ║
║            │                                    │                ║
║            └──────────────┬─────────────────────┘                ║
║                           ▼                                      ║
║              ┌────────────────────────┐                          ║
║              │    SkillRegistry       │  ← SINGLE SOURCE         ║
║              │    (27 skills, 80+     │     OF TRUTH              ║
║              │     tools)             │                           ║
║              │    IRevitSkill[]       │                           ║
║              └────────────────────────┘                          ║
╚══════════════════════════════════════════════════════════════════╝
```

### Nguyên tắc

- `SkillRegistry` là nguồn duy nhất cho tool definitions
- Developer thêm skill mới → đăng ký vào SkillRegistry → cả Chat Window và MCP đều tự động có
- Không duplicate tool definitions
- Hai ExternalEvent handler song song, dùng chung SkillRegistry (stateless)

---

## 6. Chi tiết kỹ thuật MCP Server bằng C#

### NuGet packages

| Package | Version | Project |
|---------|---------|---------|
| `ModelContextProtocol` | `0.9.0-preview.2` | RevitMcp |
| `Microsoft.Extensions.Hosting` | `9.*` | RevitMcp |

### RevitProxyTool — Dynamic MCP tool (subclass pattern)

`McpServerTool` là abstract class. Subclass cho phép tạo tools tại runtime mà không cần hardcode:

```csharp
public class RevitProxyTool : McpServerTool
{
    public override Tool ProtocolTool => new Tool
    {
        Name = _name,
        Description = _description,
        InputSchema = _inputSchema  // từ ChatTool.FunctionParameters
    };

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        // Forward qua TCP tới Revit → McpBridgeService → ExternalEvent
        // → SkillRegistry.ExecuteTool() → JSON result
    }
}
```

### IPC Protocol (JSON-RPC over TCP)

```
RevitMcp.exe ←→ McpBridgeService (TCP:8400)

Request:  { "jsonrpc": "2.0", "method": "list_tools", "params": {}, "id": "1" }
Response: { "jsonrpc": "2.0", "id": "1", "result": [...tool definitions...] }

Request:  { "jsonrpc": "2.0", "method": "count_elements", "params": {"category":"Walls"}, "id": "2" }
Response: { "jsonrpc": "2.0", "id": "2", "result": {"total": 127, ...} }
```

Messages phân cách bằng newline (`\n`) — line-delimited JSON.

### Tool definition conversion

`ChatTool` (OpenAI format) → MCP `Tool` (MCP format):

```csharp
// ChatTool đã có:
//   ct.FunctionName        → Tool.Name
//   ct.FunctionDescription → Tool.Description
//   ct.FunctionParameters  → Tool.InputSchema (cả hai đều là JSON Schema)
```

Conversion gần như 1:1, chỉ cần extract fields.

### Flow hoàn chỉnh

```
1. User mở Revit → bấm "Start MCP Bridge"
   → McpBridgeService starts TcpListener on port 8400

2. User mở Claude Desktop → Claude launches RevitMcp.exe
   → RevitMcp.exe connects TCP to localhost:8400
   → Sends: { method: "list_tools" }
   → Nhận 80+ tool definitions từ SkillRegistry
   → Tạo 80+ RevitProxyTool instances
   → MCP server ready (stdio)

3. User hỏi Claude: "Đếm số tường trong model"
   → Claude chọn tool "count_elements"
   → RevitProxyTool.InvokeAsync()
   → TCP: { method: "count_elements", params: { category: "Walls" } }
   → McpBridgeService nhận → enqueue McpRequestQueue
   → ExternalEvent.Raise()
   → RevitMcpHandler.Execute() (main thread)
   → SkillRegistry.ExecuteTool("count_elements", app, args)
   → JSON result ← TCP ← MCP server ← Claude

4. Claude hiển thị kết quả
```

---

## 7. Kế hoạch triển khai 6 Phase

### Phase 1: MCP Bridge trong Revit (2-3 ngày)

| Task | File | Mô tả |
|------|------|-------|
| 1.1 | `src/RevitChat/Handler/McpBridgeService.cs` | TCP server (TcpListener, port 8400, loopback only) |
| 1.2 | `src/RevitChat/Handler/McpRequestQueue.cs` | Queue + `TaskCompletionSource<string>` |
| 1.3 | `src/RevitChat/Handler/RevitMcpHandler.cs` | `IExternalEventHandler`, dequeue → `SkillRegistry.ExecuteTool()` |
| 1.4 | `src/RevitChat/Models/McpToolCallRequest.cs` | Request model với TCS |
| 1.5 | `src/RevitChat/Handler/ToolDefinitionExporter.cs` | Convert `ChatTool` → JSON |
| 1.6 | Cập nhật `src/RevitChat/Entry.cs` | Thêm `StartMcpBridge(uiapp)`, shared `_skillRegistry` |

**Verify**: TCP client gửi `list_tools` → nhận 80+ definitions.

### Phase 2: RevitMcp.exe — MCP Server (2-3 ngày)

| Task | File | Mô tả |
|------|------|-------|
| 2.1 | `src/RevitMcp/RevitMcp.csproj` | Console app net8.0 |
| 2.2 | `src/RevitMcp/Program.cs` | Host builder + stdio transport |
| 2.3 | `src/RevitMcp/RevitProxyTool.cs` | Subclass `McpServerTool` |
| 2.4 | `src/RevitMcp/RevitConnection.cs` | TCP client, persistent, retry |
| 2.5 | `src/RevitMcp/RevitToolRegistrar.cs` | `IHostedService`, connect + register |
| 2.6 | `src/RevitMcp/Models/ToolDefinition.cs` | DTO |

**Verify**: Claude Desktop → hỏi về model → nhận kết quả từ Revit.

**Fallback**: Nếu C# MCP SDK có issue → tự implement MCP JSON-RPC over stdio (~300 dòng).

### Phase 3: pyRevit Integration & Build (1-2 ngày)

| Task | File | Mô tả |
|------|------|-------|
| 3.1 | `HD.extension/.../McpBridge.pushbutton/script.py` | Thin launcher |
| 3.2 | Cập nhật `build-release.ps1` | Thêm RevitMcp publish |
| 3.3 | `install-mcp.ps1` | Auto-configure `claude_desktop_config.json` |
| 3.4 | `Data/Config/mcp_config.json` | Port, auto-start settings |

### Phase 4: Tool Sync Verification (1-2 ngày)

| Task | Mô tả |
|------|-------|
| 4.1 | Kiểm tra tất cả 27 skills hoạt động qua MCP |
| 4.2 | Thêm `ping_revit`, `get_tool_count` tools |
| 4.3 | Tạo skill template cho developer |
| 4.4 | Verify: thêm skill mới → tự động có ở cả 2 paths |

### Phase 5: Claude-Optimized Experience (1-2 ngày)

| Task | Mô tả |
|------|-------|
| 5.1 | Đánh dấu `readOnly` / `destructive` cho tools |
| 5.2 | Thêm `get_active_context` tool (view, selection, level) |
| 5.3 | MCP Resource: project summary |
| 5.4 | Vietnamese + English tool descriptions |

### Phase 6: Polish & Advanced (2-3 ngày)

| Task | Mô tả |
|------|-------|
| 6.1 | Connection status UI trên Ribbon |
| 6.2 | Auto-reconnect trong RevitMcp.exe |
| 6.3 | Logging: `mcp_bridge.log` |
| 6.4 | Multi-client (Claude + Cursor đồng thời) |
| 6.5 | "Copy MCP Config" button |
| 6.6 | README, documentation |
| 6.7 | Test trên máy khác |

### Timeline

```
Tuần 1: Phase 1 + Phase 2 (foundation)
Tuần 2: Phase 3 + Phase 4 + Phase 5 (integration)
Tuần 3: Phase 6 (polish)
Tổng: ~10-15 ngày
```

---

## 8. Files cần tạo và sửa

### Files MỚI

| File | Project | Mô tả |
|------|---------|-------|
| `src/RevitMcp/RevitMcp.csproj` | RevitMcp | Console app MCP server |
| `src/RevitMcp/Program.cs` | RevitMcp | Entry point |
| `src/RevitMcp/RevitProxyTool.cs` | RevitMcp | Dynamic MCP tool proxy |
| `src/RevitMcp/RevitConnection.cs` | RevitMcp | TCP client to Revit |
| `src/RevitMcp/RevitToolRegistrar.cs` | RevitMcp | Startup tool loader |
| `src/RevitMcp/Models/ToolDefinition.cs` | RevitMcp | DTO |
| `src/RevitChat/Handler/McpBridgeService.cs` | RevitChat | TCP server in Revit |
| `src/RevitChat/Handler/RevitMcpHandler.cs` | RevitChat | ExternalEvent for MCP |
| `src/RevitChat/Handler/McpRequestQueue.cs` | RevitChat | Queue + TCS |
| `src/RevitChat/Models/McpToolCallRequest.cs` | RevitChat | Request model |
| `src/RevitChat/Handler/ToolDefinitionExporter.cs` | RevitChat | ChatTool → MCP format |
| `HD.extension/.../McpBridge.pushbutton/script.py` | pyRevit | Thin launcher |
| `install-mcp.ps1` | Root | Auto-configure Claude Desktop |

### Files CẦN SỬA

| File | Thay đổi |
|------|----------|
| `src/RevitChat/Entry.cs` | Thêm `StartMcpBridge()`, shared `_skillRegistry` |
| `build-release.ps1` | Thêm RevitMcp build + publish |

### Files KHÔNG SỬA

Tất cả 27 `*Skill.cs`, `SkillRegistry.cs`, `IRevitSkill.cs`, `RevitChatHandler.cs`, `OllamaChatService.cs`, các `script.py` hiện có — không bị ảnh hưởng.

---

## 9. Lưu ý kỹ thuật

### 1. Threading

- Revit API **chỉ chạy trên main thread**
- `McpBridgeService` (TCP) chạy background thread → KHÔNG gọi Revit API
- `RevitMcpHandler.Execute()` chạy main thread → gọi `SkillRegistry.ExecuteTool()`
- `McpRequestQueue` + `TaskCompletionSource` làm cầu nối

### 2. ExternalEvent latency

- `ExternalEvent.Raise()` không chạy ngay (100ms–5s tùy Revit state)
- Timeout 60s cho mỗi tool call, không để pipe treo
- Nếu timeout → trả error rõ ràng, không crash MCP server

### 3. Startup order

```
ĐÚNG: Revit → mở project → "Start MCP Bridge" → mở Claude Desktop
SAI:  Claude Desktop → RevitMcp.exe → Revit chưa sẵn sàng → lỗi
```

RevitMcp.exe retry kết nối 15 lần × 2s = 30s timeout.

### 4. Hai ExternalEvent handler song song

- `RevitChatHandler` (Chat Window) + `RevitMcpHandler` (MCP) — ExternalEvent riêng
- Dùng chung `SkillRegistry` (stateless, an toàn)
- Revit xử lý tuần tự, không xung đột

### 5. TCP message framing

- Line-delimited JSON (`\n` separator)
- Buffer tích lũy bytes đến khi gặp `\n`
- Xử lý partial messages và multiple messages per read

### 6. C# MCP SDK prerelease

- Pin version `0.9.0-preview.2`, không dùng wildcard
- Test `McpServerTool` subclass pattern sớm
- Backup: tự implement MCP JSON-RPC (~300 dòng)

### 7. Port conflict

- Port configurable qua `mcp_config.json`
- Mặc định 8400 (tránh xung đột 8080)
- Try-catch `AddressInUseException`, thông báo rõ

### 8. Bảo mật

- TCP bind `IPAddress.Loopback` (127.0.0.1) only
- Destructive tools đánh dấu `destructive: true`
- Không gửi credentials qua TCP

### 9. Large responses

- Giữ `limit` parameter trong query tools
- Truncate nếu > 50KB
- Claude tự quản lý context window

### 10. Tool sync guarantee

```
Developer thêm skill mới:
1. Tạo XxxSkill.cs (implement IRevitSkill)
2. Register trong SkillRegistry.CreateDefault()
3. Build → DONE
   ✅ Chat Window: tự động có (GetAllToolDefinitions)
   ✅ MCP/Claude: tự động có (list_tools → SkillRegistry)
```

---

## 10. Tài nguyên cần chuẩn bị

### Phần mềm

| Tài nguyên | Mục đích | Kiểm tra |
|-----------|---------|----------|
| .NET 8 SDK | Build projects | `dotnet --version` → 8.x.x |
| Revit 2025/2026 | Test runtime | Đã có |
| pyRevit | Load extension | Đã có |
| Claude Desktop | Test MCP client | [claude.ai/download](https://claude.ai/download) |
| Claude Pro/Team | MCP support | Kiểm tra account |

### NuGet packages mới

| Package | Project |
|---------|---------|
| `ModelContextProtocol` (0.9.0-preview.2) | RevitMcp |
| `Microsoft.Extensions.Hosting` (9.*) | RevitMcp |

### Tài liệu tham khảo

| Tài liệu | URL |
|----------|-----|
| MCP C# SDK docs | https://modelcontextprotocol.github.io/csharp-sdk/ |
| EverythingServer sample | https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Program.cs |
| revit-mcp-plugin | https://github.com/mcp-servers-for-revit/revit-mcp-plugin |
| MCP specification | https://modelcontextprotocol.io/specification/ |
| Claude Desktop MCP config | https://modelcontextprotocol.io/quickstart |
| Microsoft MCP blog | https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/ |

### Kiểm tra trước khi code

| # | Kiểm tra | Cách | Pass |
|---|---------|------|------|
| 1 | .NET 8 SDK | `dotnet --version` | 8.x.x |
| 2 | Build hiện tại OK | `dotnet build src/RevitChat/RevitChat.csproj` | Succeeded |
| 3 | Claude Desktop cài | Mở app | OK |
| 4 | Claude có MCP support | Settings → Developer → Edit Config | File tồn tại |
| 5 | Port 8400 free | `netstat -ano \| findstr 8400` | Không có |
| 6 | MCP SDK tải được | `dotnet add package ModelContextProtocol --prerelease` | OK |
| 7 | Revit add-in hoạt động | Revit → HD tab → RevitChatLocal | Chat mở được |

### Codebase cần nắm vững

| File | Lý do |
|------|-------|
| `src/RevitChat/Skills/IRevitSkill.cs` | Interface mọi skill implement |
| `src/RevitChat/Skills/SkillRegistry.cs` | Đăng ký tools, cần dùng chung |
| `src/RevitChat/Handler/RevitChatHandler.cs` | Pattern ExternalEvent, clone cho MCP |
| `src/RevitChat/Handler/ChatRequestQueue.cs` | Pattern queue, mở rộng thêm TCS |
| `src/RevitChat/Entry.cs` | Cần sửa, thêm StartMcpBridge |
| `src/RevitChat/Skills/QuerySkill.cs` | Mẫu ChatTool definition |
| `build-release.ps1` | Thêm RevitMcp vào pipeline |

---

## Rủi ro và backup

| Rủi ro | Xác suất | Backup |
|--------|----------|--------|
| C# MCP SDK bug với dynamic tools | Trung bình | Tự implement MCP JSON-RPC (~300 dòng) |
| ExternalEvent latency cao | Thấp | Retry + tăng timeout |
| Port conflict | Thấp | Configurable port |
| Claude không nhận RevitMcp.exe | Thấp | Fallback TypeScript MCP server |
| 80+ tools overload Claude | Trung bình | Skill pack filtering |

---

## Đảm bảo tương thích ngược

| Tính năng hiện có | Ảnh hưởng |
|-------------------|-----------|
| RevitChat (OpenAI) | Không |
| RevitChatLocal (Ollama) | Không |
| SmartTag | Không |
| CommonFeature | Không |
| CheckCode | Không |
| pyRevit launchers | Không |
| build-release.ps1 | Chỉ thêm, không sửa existing |

Toàn bộ hệ thống mới **thêm vào bên cạnh**, không sửa đổi code đang hoạt động.
