# HD AI Connector — MCP Architecture Plan

> Comprehensive research, architecture, and implementation plan for a Revit MCP Server.
> Created: 2026-02-22

---

## Table of Contents

1. [NonicaTab AI Connector Research](#1-nonicatab-ai-connector-research)
2. [Current Codebase Analysis](#2-current-codebase-analysis)
3. [Comparison with NonicaTab](#3-comparison-with-nonicatab)
4. [Reference: revit-mcp-plugin (open source)](#4-reference-revit-mcp-plugin-open-source)
5. [Target Architecture](#5-target-architecture)
6. [Technical Details: C# MCP Server](#6-technical-details-c-mcp-server)
7. [6-Phase Implementation Plan](#7-6-phase-implementation-plan)
8. [Files to Create and Modify](#8-files-to-create-and-modify)
9. [Technical Notes](#9-technical-notes)
10. [Required Resources](#10-required-resources)

---

## 1. NonicaTab AI Connector Research

### Origin

A video on the BIM Pure channel (12/01/2026) introduced **NonicaTab** — a Revit extension that connects to Claude Desktop via Model Context Protocol (MCP).

### How It Works

- NonicaTab provides 50+ specialized tools for AI to read/write Revit model data
- AI calls tools via MCP (does not generate Revit code itself)
- Data is processed locally, sent to AI per privacy policy
- Requirements: Revit 2022+, NonicaTab + AI Connector installed, Claude Desktop open

### Key Features

| Feature | Description |
|---------|-------------|
| Comprehensive Reports | Element, type, parameter, and project statistics |
| Performance Analysis | Warning checks, bottleneck identification |
| Accessibility Checks | ADA compliance evaluation |
| Schedule Extraction | Create charts, dashboards |
| Quality Control | Fix parameters, incorrect formatting |
| View/Sheet Creation | Room elevations, sheet layout |
| Multi-agent | Multiple AIs on different tasks in parallel |

### NonicaTab Architecture

```
Claude Desktop ←(MCP/stdio)→ NonicaTab MCP Server ←→ Revit Add-in
```

- MCP Server runs locally, exposes tools via JSON-RPC
- AI clients (Claude, Copilot, Cursor) connect via MCP protocol
- Free version: ~37 tools (read/analyze)
- Pro version: 50+ tools (read + write)

---

## 2. Current Codebase Analysis

### Projects

| Project | Target | Purpose |
|---------|--------|---------|
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

### Current AI Chat Architecture

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

### Key Interfaces

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
    public static SkillRegistry CreateDefault();
}
```

---

## 3. Comparison with NonicaTab

| Feature | NonicaTab | Current Codebase | Notes |
|---------|-----------|------------------|-------|
| Tool Count | ~50 (Pro) | **80+ tools** | Advantage |
| Read/Analyze | Yes | **QuerySkill, ProjectInfoSkill, ModelHealthSkill** | Full coverage |
| Write/Modify | Pro only | **ModifySkill** (set/delete/rename/copy/move/mirror) | Full coverage |
| MEP Tools | Unclear | **7 MEP skills** | Advantage |
| BIM Coordination | Yes | **ClashDetection, CoordinationReport, NamingAudit, PurgeAudit** | Full coverage |
| View/Sheet | Yes | **ViewControlSkill, SheetManagementSkill** | Full coverage |
| AI Protocol | **MCP** | **OpenAI Function Calling** | Key difference |
| AI Backend | Claude Desktop (external) | **OpenAI API + Ollama local** | Both cloud + local |
| Chat UI | External app | **WPF modeless window** | Better UX |
| Multi-agent | Yes | Not yet | Gap |
| Vietnamese | No | **Yes** | Advantage |
| Feedback/Learning | Unclear | **ChatFeedbackService** | Already built |

**Conclusion**: ~80% parity with NonicaTab. Main missing piece: **MCP protocol layer**.

---

## 4. Reference: revit-mcp-plugin (open source)

**Repo**: https://github.com/mcp-servers-for-revit/revit-mcp-plugin

### 3-Part Architecture

```
Claude Desktop ←(stdio)→ revit-mcp (TypeScript/Node.js) ←(TCP:8080)→ revit-mcp-plugin (C#)
```

1. **revit-mcp** (TypeScript): MCP Server, stdio transport, hardcoded tool definitions
2. **revit-mcp-plugin** (C#): Revit add-in, TcpListener:8080, JSON-RPC, ExternalEvent
3. **revit-mcp-commandset** (C#): External DLLs loaded at runtime

### Lessons Learned

| Lesson | Details | Application |
|--------|---------|-------------|
| TCP Socket works well | Simple, easy to debug, supports remote | Use TCP instead of Named Pipe |
| Tool definitions duplicated | TypeScript defines schema, C# defines logic → 2 places | Avoid: use dynamic discovery |
| Connection per request | Each tool call creates a new connection | Improve: persistent connection |
| JSON-RPC standard is solid | Both MCP and IPC use JSON-RPC | Use JSON-RPC for IPC |
| 15 tools, no MEP | Much smaller scope | We have 80+ tools |
| TypeScript MCP server | MCP SDK for TypeScript was released first | Use C# SDK (available now) |

---

## 5. Target Architecture

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

### Principles

- `SkillRegistry` is the single source of truth for tool definitions
- Adding a new skill → register in SkillRegistry → both Chat Window and MCP automatically have it
- No duplicate tool definitions
- Two ExternalEvent handlers in parallel, sharing a stateless SkillRegistry

---

## 6. Technical Details: C# MCP Server

### NuGet Packages

| Package | Version | Project |
|---------|---------|---------|
| `ModelContextProtocol` | `0.9.0-preview.2` | RevitMcp |
| `Microsoft.Extensions.Hosting` | `9.*` | RevitMcp |

### RevitProxyTool — Dynamic MCP Tool (Subclass Pattern)

`McpServerTool` is an abstract class. Subclassing allows creating tools at runtime without hardcoding:

```csharp
public class RevitProxyTool : McpServerTool
{
    public override Tool ProtocolTool => new Tool
    {
        Name = _name,
        Description = _description,
        InputSchema = _inputSchema
    };

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        // Forward via TCP to Revit → McpBridgeService → ExternalEvent
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

Messages delimited by newline (`\n`) — line-delimited JSON.

### Tool Definition Conversion

`ChatTool` (OpenAI format) → MCP `Tool` (MCP format):

```csharp
// ChatTool already has:
//   ct.FunctionName        → Tool.Name
//   ct.FunctionDescription → Tool.Description
//   ct.FunctionParameters  → Tool.InputSchema (both are JSON Schema)
```

Conversion is nearly 1:1 — just extract fields.

### Complete Flow

```
1. User opens Revit → clicks "Start MCP Bridge"
   → McpBridgeService starts TcpListener on port 8400

2. User opens Claude Desktop → Claude launches RevitMcp.exe
   → RevitMcp.exe connects TCP to localhost:8400
   → Sends: { method: "list_tools" }
   → Receives 80+ tool definitions from SkillRegistry
   → Creates 80+ RevitProxyTool instances
   → MCP server ready (stdio)

3. User asks Claude: "Count all walls in the model"
   → Claude selects tool "count_elements"
   → RevitProxyTool.InvokeAsync()
   → TCP: { method: "count_elements", params: { category: "Walls" } }
   → McpBridgeService receives → enqueue McpRequestQueue
   → ExternalEvent.Raise()
   → RevitMcpHandler.Execute() (main thread)
   → SkillRegistry.ExecuteTool("count_elements", app, args)
   → JSON result ← TCP ← MCP server ← Claude

4. Claude displays the result
```

---

## 7. 6-Phase Implementation Plan

### Phase 1: MCP Bridge in Revit (2-3 days)

| Task | File | Description |
|------|------|-------------|
| 1.1 | `src/RevitChat/Handler/McpBridgeService.cs` | TCP server (TcpListener, port 8400, loopback only) |
| 1.2 | `src/RevitChat/Handler/McpRequestQueue.cs` | Queue + `TaskCompletionSource<string>` |
| 1.3 | `src/RevitChat/Handler/RevitMcpHandler.cs` | `IExternalEventHandler`, dequeue → `SkillRegistry.ExecuteTool()` |
| 1.4 | `src/RevitChat/Models/McpToolCallRequest.cs` | Request model with TCS |
| 1.5 | `src/RevitChat/Handler/ToolDefinitionExporter.cs` | Convert `ChatTool` → JSON |
| 1.6 | Update `src/RevitChat/Entry.cs` | Add `StartMcpBridge(uiapp)`, shared `_skillRegistry` |

**Verify**: TCP client sends `list_tools` → receives 80+ definitions.

### Phase 2: RevitMcp.exe — MCP Server (2-3 days)

| Task | File | Description |
|------|------|-------------|
| 2.1 | `src/RevitMcp/RevitMcp.csproj` | Console app net8.0 |
| 2.2 | `src/RevitMcp/Program.cs` | Host builder + stdio transport |
| 2.3 | `src/RevitMcp/RevitProxyTool.cs` | Subclass `McpServerTool` |
| 2.4 | `src/RevitMcp/RevitConnection.cs` | TCP client, persistent, retry |
| 2.5 | `src/RevitMcp/RevitToolRegistrar.cs` | `IHostedService`, connect + register |
| 2.6 | `src/RevitMcp/Models/ToolDefinition.cs` | DTO |

**Verify**: Claude Desktop → ask about model → receive results from Revit.

**Fallback**: If C# MCP SDK has issues → implement MCP JSON-RPC over stdio manually (~300 lines).

### Phase 3: pyRevit Integration & Build (1-2 days)

| Task | File | Description |
|------|------|-------------|
| 3.1 | `HD.extension/.../McpBridge.pushbutton/script.py` | Thin launcher |
| 3.2 | Update `build-release.ps1` | Add RevitMcp publish |
| 3.3 | `install-mcp.ps1` | Auto-configure `claude_desktop_config.json` |
| 3.4 | `Data/Config/mcp_config.json` | Port, auto-start settings |

### Phase 4: Tool Sync Verification (1-2 days)

| Task | Description |
|------|-------------|
| 4.1 | Verify all 27 skills work via MCP |
| 4.2 | Add `ping_revit`, `get_tool_count` tools |
| 4.3 | Create skill template for developers |
| 4.4 | Verify: adding new skill → automatically available in both paths |

### Phase 5: Claude-Optimized Experience (1-2 days)

| Task | Description |
|------|-------------|
| 5.1 | Mark tools as `readOnly` / `destructive` |
| 5.2 | Add `get_active_context` tool (view, selection, level) |
| 5.3 | MCP Resource: project summary |
| 5.4 | Vietnamese + English tool descriptions |

### Phase 6: Polish & Advanced (2-3 days)

| Task | Description |
|------|-------------|
| 6.1 | Connection status UI on Ribbon |
| 6.2 | Auto-reconnect in RevitMcp.exe |
| 6.3 | Logging: `mcp_bridge.log` |
| 6.4 | Multi-client (Claude + Cursor simultaneously) |
| 6.5 | "Copy MCP Config" button |
| 6.6 | README, documentation |
| 6.7 | Test on another machine |

### Timeline

```
Week 1: Phase 1 + Phase 2 (foundation)
Week 2: Phase 3 + Phase 4 + Phase 5 (integration)
Week 3: Phase 6 (polish)
Total: ~10-15 days
```

---

## 8. Files to Create and Modify

### NEW Files

| File | Project | Description |
|------|---------|-------------|
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

### Files to MODIFY

| File | Change |
|------|--------|
| `src/RevitChat/Entry.cs` | Add `StartMcpBridge()`, shared `_skillRegistry` |
| `build-release.ps1` | Add RevitMcp build + publish |

### Files NOT Modified

All 27 `*Skill.cs`, `SkillRegistry.cs`, `IRevitSkill.cs`, `RevitChatHandler.cs`, `OllamaChatService.cs`, existing `script.py` files — unaffected.

---

## 9. Technical Notes

### 1. Threading

- Revit API **runs only on the main thread**
- `McpBridgeService` (TCP) runs on background thread → does NOT call Revit API
- `RevitMcpHandler.Execute()` runs on main thread → calls `SkillRegistry.ExecuteTool()`
- `McpRequestQueue` + `TaskCompletionSource` bridges the two

### 2. ExternalEvent Latency

- `ExternalEvent.Raise()` does not execute immediately (100ms–5s depending on Revit state)
- 60s timeout per tool call to prevent pipe hangs
- On timeout → return clear error, do not crash MCP server

### 3. Startup Order

```
CORRECT: Revit → open project → "Start MCP Bridge" → open Claude Desktop
WRONG:   Claude Desktop → RevitMcp.exe → Revit not ready → error
```

RevitMcp.exe retries connection 15 times x 2s = 30s timeout.

### 4. Two Parallel ExternalEvent Handlers

- `RevitChatHandler` (Chat Window) + `RevitMcpHandler` (MCP) — separate ExternalEvents
- Share `SkillRegistry` (stateless, safe)
- Revit processes them sequentially, no conflicts

### 5. TCP Message Framing

- Line-delimited JSON (`\n` separator)
- Buffer accumulates bytes until `\n` encountered
- Handles partial messages and multiple messages per read

### 6. C# MCP SDK Prerelease

- Pin version `0.9.0-preview.2`, do not use wildcards
- Test `McpServerTool` subclass pattern early
- Backup: implement MCP JSON-RPC manually (~300 lines)

### 7. Port Conflicts

- Port configurable via `mcp_config.json`
- Default 8400 (avoids 8080 conflicts)
- Try-catch `AddressInUseException`, report clearly

### 8. Security

- TCP binds `IPAddress.Loopback` (127.0.0.1) only
- Destructive tools marked `destructive: true`
- No credentials sent over TCP

### 9. Large Responses

- Keep `limit` parameter in query tools
- Truncate if > 50KB
- Claude manages its own context window

### 10. Tool Sync Guarantee

```
Developer adds a new skill:
1. Create XxxSkill.cs (implement IRevitSkill)
2. Register in SkillRegistry.CreateDefault()
3. Build → DONE
   ✅ Chat Window: automatically available (GetAllToolDefinitions)
   ✅ MCP/Claude: automatically available (list_tools → SkillRegistry)
```

---

## 10. Required Resources

### Software

| Resource | Purpose | Check |
|----------|---------|-------|
| .NET 8 SDK | Build projects | `dotnet --version` → 8.x.x |
| Revit 2025/2026 | Test runtime | Available |
| pyRevit | Load extension | Available |
| Claude Desktop | Test MCP client | [claude.ai/download](https://claude.ai/download) |
| Claude Pro/Team | MCP support | Check account |

### New NuGet Packages

| Package | Project |
|---------|---------|
| `ModelContextProtocol` (0.9.0-preview.2) | RevitMcp |
| `Microsoft.Extensions.Hosting` (9.*) | RevitMcp |

### Reference Documentation

| Document | URL |
|----------|-----|
| MCP C# SDK docs | https://modelcontextprotocol.github.io/csharp-sdk/ |
| EverythingServer sample | https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Program.cs |
| revit-mcp-plugin | https://github.com/mcp-servers-for-revit/revit-mcp-plugin |
| MCP specification | https://modelcontextprotocol.io/specification/ |
| Claude Desktop MCP config | https://modelcontextprotocol.io/quickstart |
| Microsoft MCP blog | https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/ |

### Pre-Coding Checklist

| # | Check | Method | Pass |
|---|-------|--------|------|
| 1 | .NET 8 SDK | `dotnet --version` | 8.x.x |
| 2 | Current build OK | `dotnet build src/RevitChat/RevitChat.csproj` | Succeeded |
| 3 | Claude Desktop installed | Open app | OK |
| 4 | Claude has MCP support | Settings → Developer → Edit Config | File exists |
| 5 | Port 8400 free | `netstat -ano \| findstr 8400` | Not in use |
| 6 | MCP SDK downloadable | `dotnet add package ModelContextProtocol --prerelease` | OK |
| 7 | Revit add-in works | Revit → HD tab → RevitChatLocal | Chat opens |

### Key Codebase Files

| File | Reason |
|------|--------|
| `src/RevitChat/Skills/IRevitSkill.cs` | Interface all skills implement |
| `src/RevitChat/Skills/SkillRegistry.cs` | Tool registry, must be shared |
| `src/RevitChat/Handler/RevitChatHandler.cs` | ExternalEvent pattern, clone for MCP |
| `src/RevitChat/Handler/ChatRequestQueue.cs` | Queue pattern, extend with TCS |
| `src/RevitChat/Entry.cs` | Needs modification, add StartMcpBridge |
| `src/RevitChat/Skills/QuerySkill.cs` | Sample ChatTool definition |
| `build-release.ps1` | Add RevitMcp to pipeline |

---

## Risks and Backup Plans

| Risk | Probability | Backup |
|------|-------------|--------|
| C# MCP SDK bug with dynamic tools | Medium | Implement MCP JSON-RPC manually (~300 lines) |
| High ExternalEvent latency | Low | Retry + increase timeout |
| Port conflict | Low | Configurable port |
| Claude doesn't recognize RevitMcp.exe | Low | Fallback TypeScript MCP server |
| 80+ tools overloading Claude | Medium | Skill pack filtering |

---

## Backward Compatibility Guarantee

| Existing Feature | Impact |
|-----------------|--------|
| RevitChat (OpenAI) | None |
| RevitChatLocal (Ollama) | None |
| SmartTag | None |
| CommonFeature | None |
| CheckCode | None |
| pyRevit launchers | None |
| build-release.ps1 | Addition only, no existing modifications |

The entire new system is **added alongside** existing code, without modifying anything currently working.
