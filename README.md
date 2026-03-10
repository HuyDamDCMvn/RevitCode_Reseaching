# HD Extension for Revit

A comprehensive pyRevit extension with C# DLL backend for Revit 2025+, featuring AI-powered chatbot, smart auto-tagging, and BIM utilities.

---

## High-Level Architecture

```mermaid
flowchart TB
    subgraph User["User in Revit"]
        REV["Autodesk Revit 2025/2026"]
    end

    subgraph pyRevit["pyRevit Extension Layer"]
        TAB["HD.tab"]
        PY["script.py<br/>(thin launcher — detect version, load DLL)"]
    end

    subgraph DLLs["C# DLL Backend (net8.0-windows)"]
        CORE["HD.Core<br/>Shared services & helpers"]
        CHAT["RevitChat<br/>AI Skills & SkillRegistry"]
        LOCAL["RevitChatLocal<br/>Ollama Chat UI + Service"]
        TAG["SmartTag<br/>Auto-tag & ML placement"]
        FEAT["CommonFeature<br/>BIM utilities & tools"]
        CHK["CheckCode<br/>Model checking"]
    end

    subgraph AI["AI Backend"]
        OLLAMA["Ollama (local)<br/>qwen2.5:7b"]
        OPENAI["OpenAI API (cloud)"]
    end

    REV --> TAB
    TAB --> PY
    PY -->|"clr.AddReference"| DLLs
    LOCAL --> OLLAMA
    CHAT --> OPENAI
    CORE -.->|shared by all| CHAT
    CORE -.->|shared by all| LOCAL
    CORE -.->|shared by all| TAG
    CORE -.->|shared by all| FEAT
    CORE -.->|shared by all| CHK

    style User fill:#e3f2fd,stroke:#1565C0
    style pyRevit fill:#f3e5f5,stroke:#7B1FA2
    style DLLs fill:#e8f5e9,stroke:#2E7D32
    style AI fill:#fff3e0,stroke:#EF6C00
```

---

## Project & DLL Dependency Map

```mermaid
flowchart LR
    subgraph Projects["C# Projects (src/)"]
        direction TB
        HDCore["HD.Core<br/><i>Shared library</i>"]
        RevitChat["RevitChat<br/><i>Skills, Handler, ViewModel,<br/>Learning, SkillRegistry</i>"]
        RevitChatLocal["RevitChatLocal<br/><i>Ollama service, WPF UI,<br/>LocalChatViewModel</i>"]
        SmartTag["SmartTag<br/><i>Tag placement, ML,<br/>RuleEngine, CSP</i>"]
        CommonFeature["CommonFeature<br/><i>BIM tools, Boundary,<br/>Parameter editor</i>"]
        CheckCode["CheckCode<br/><i>Model quality checks</i>"]
    end

    RevitChat --> HDCore
    RevitChatLocal --> HDCore
    RevitChatLocal --> RevitChat
    SmartTag --> HDCore
    CommonFeature --> HDCore
    CheckCode --> HDCore

    style HDCore fill:#bbdefb,stroke:#1565C0,stroke-width:2px
    style RevitChat fill:#c8e6c9,stroke:#2E7D32
    style RevitChatLocal fill:#c8e6c9,stroke:#2E7D32
    style SmartTag fill:#fff9c4,stroke:#F9A825
    style CommonFeature fill:#ffe0b2,stroke:#EF6C00
    style CheckCode fill:#f8bbd0,stroke:#C2185B
```

---

## pyRevit Extension Layout

```mermaid
flowchart TD
    subgraph Extension["HD.extension"]
        subgraph Lib["lib/"]
            NET8["net8/<br/>All DLLs + dependencies"]
            DATA["net8/Data/<br/>ChatConfig, Rules, Patterns,<br/>Models, Feedback"]
            LAUNCH["launcher_base.py"]
        end

        subgraph Tab["HD.tab"]
            subgraph AI["AI.panel"]
                BTN1["RevitChat.pushbutton"]
                BTN2["RevitChatLocal.pushbutton"]
            end
            subgraph Label["Labeling.panel"]
                BTN3["SmartTag.pushbutton"]
            end
            subgraph Gen["General.panel"]
                BTN4["Extension.pushbutton"]
                BTN5["Setting.pushbutton"]
                BTN6["Reload.pushbutton"]
            end
            subgraph WIP["WIP.panel"]
                BTN7["CommonFeature.pushbutton"]
                BTN8["CheckCode.pushbutton"]
            end
        end
    end

    BTN1 & BTN2 & BTN3 & BTN7 & BTN8 -->|"script.py<br/>thin launcher"| NET8

    style Extension fill:#f5f5f5,stroke:#616161
    style Lib fill:#e8eaf6,stroke:#283593
    style Tab fill:#efebe9,stroke:#4E342E
```

---

## RevitChat — AI Chatbot Flow

```mermaid
sequenceDiagram
    actor User
    participant UI as Chat Window<br/>(WPF)
    participant VM as BaseChatViewModel
    participant CS as OllamaChatService
    participant LLM as Ollama LLM
    participant Q as ChatRequestQueue
    participant EE as ExternalEvent
    participant H as RevitChatHandler
    participant SR as SkillRegistry<br/>(170+ tools)

    User->>UI: Type message
    UI->>VM: SendCommand

    Note over VM: Analyze + Build Prompt
    VM->>CS: SendMessageAsync()
    CS->>LLM: POST /v1/chat/completions
    LLM-->>CS: tool_call response

    Note over VM: Execute on Revit Thread
    VM->>Q: Enqueue(tool request)
    VM->>EE: Raise()
    EE->>H: Execute(UIApplication)
    H->>SR: ExecuteTool(name, args)
    SR-->>H: JSON result
    H-->>VM: OnToolCallsCompleted

    Note over VM: Summarize Result
    VM->>CS: ContinueWithToolResults()
    CS->>LLM: Summarize
    LLM-->>CS: Natural language answer
    CS-->>VM: Final response
    VM->>UI: Display to user

    Note over VM: Learn from Interaction
    VM->>VM: RecordLearningSignals()
```

---

## SmartTag — Tag Placement Pipeline

```mermaid
flowchart TB
    subgraph Input["Input"]
        SEL["User selects categories<br/>& settings"]
    end

    subgraph Collect["1. Collection"]
        EC["ElementCollector<br/>Get taggable elements"]
        ET["Get existing tags<br/>& annotations"]
    end

    subgraph Place["2. Placement"]
        RULE["RuleEngine<br/>Load JSON rules"]
        PAT["PatternLoader<br/>Load position patterns"]
        CAND["Generate candidates<br/>(9 positions × N tiers)"]
        SCORE["ScorePlacement<br/>(collision, preference,<br/>alignment, distance)"]
        BEST["Select best<br/>no-collision position"]
    end

    subgraph Resolve["3. Collision Resolution"]
        PUSH1["Push away from<br/>annotations & clearance"]
        PUSH2["Push tags apart<br/>(tag–tag)"]
    end

    subgraph Refine["4. Refinement"]
        ALIGN["Align rows & columns"]
        REPL["Re-place overlapping tags"]
        ITER["Iterate up to 3×"]
    end

    subgraph ML["ML Enhancement"]
        KNN["KNN Matcher"]
        RL["RL Agent (ONNX)"]
        FB["User Feedback"]
    end

    subgraph Output["5. Output"]
        CREATE["TagCreationService<br/>Create tags in Revit"]
    end

    SEL --> Collect
    EC --> Place
    ET --> Place
    RULE --> CAND
    PAT --> CAND
    CAND --> SCORE
    SCORE --> BEST
    BEST --> Resolve
    PUSH1 --> PUSH2
    PUSH2 --> Refine
    ALIGN --> REPL
    REPL --> ITER
    ITER -->|"still overlap"| PUSH1
    ITER -->|"clean"| Output

    ML -.->|"future"| SCORE

    style Input fill:#e3f2fd,stroke:#1565C0
    style Place fill:#e8f5e9,stroke:#2E7D32
    style Resolve fill:#fff3e0,stroke:#EF6C00
    style Refine fill:#fce4ec,stroke:#C62828
    style ML fill:#f3e5f5,stroke:#7B1FA2,stroke-dasharray: 5 5
    style Output fill:#e0f2f1,stroke:#00695C
```

---

## Self-Learning System

```mermaid
flowchart LR
    subgraph Signals["Learning Signals"]
        S1["Tool call success"]
        S2["Fallback / no tool"]
        S3["Wrong language"]
        S4["Thumbs up/down"]
        S5["User correction"]
    end

    subgraph Learning["Learning Services"]
        SLO["SelfLearning<br/>Orchestrator"]
        CAT["Contextual<br/>AutoTrainer"]
        DFS["DynamicFewShot<br/>Selector"]
        AWM["AdaptiveWeight<br/>Manager"]
        CB["ChatBandit<br/>(RL Q-table)"]
        CTC["ChatTool<br/>Classifier (ONNX)"]
        EM["Embedding<br/>Matcher"]
    end

    subgraph Persist["Per-User JSON"]
        J1["learning_stats"]
        J2["contextual_learned"]
        J3["learned_examples"]
        J4["adaptive_weights"]
        J5["bandit_qtable"]
        J6["embeddings"]
    end

    Signals --> Learning
    Learning --> Persist
    Persist -.->|"next session"| Learning

    style Signals fill:#fff3e0,stroke:#EF6C00
    style Learning fill:#e8f5e9,stroke:#2E7D32
    style Persist fill:#f3e5f5,stroke:#7B1FA2
```

---

## Threading Model

```mermaid
flowchart LR
    subgraph Main["Revit Main Thread"]
        E["Entry.ShowTool()"]
        RH["RevitChatHandler.Execute()"]
        SR["SkillRegistry.ExecuteTool()"]
        TH["SmartTagHandler.Execute()"]
    end

    subgraph UI["WPF UI Thread"]
        W["Chat / SmartTag Window"]
        D["Dispatcher.BeginInvoke"]
    end

    subgraph BG["Background Threads"]
        VM["ViewModel.SendAsync()"]
        HTTP["OllamaChatService (HTTP)"]
        LEARN["Learning services"]
        EMB["EmbeddingMatcher"]
    end

    BG -->|"ExternalEvent.Raise()"| Main
    Main -->|"OnCompleted callback"| BG
    BG -->|"Dispatcher.BeginInvoke"| UI

    style Main fill:#ffcdd2,stroke:#C62828
    style UI fill:#fff9c4,stroke:#F9A825
    style BG fill:#c8e6c9,stroke:#2E7D32
```

**Invariant rules:**
- ViewModel **never** calls Revit API directly
- Only `ExternalEvent` handlers may call Revit API (on main thread)
- AI/HTTP calls run on background threads, **never** inside ExternalEvent handlers

---

## Build & Deploy Flow

```mermaid
flowchart LR
    subgraph Dev["Development"]
        SRC["src/*.csproj"]
        BUILD["dotnet build -c Release"]
    end

    subgraph Package["Packaging"]
        DLL["Compiled DLLs"]
        COPY["Copy to HD.extension/lib/net8/"]
        JSON["Copy Data/ChatConfig/*.json"]
    end

    subgraph Deploy["Deployment"]
        PYREVIT["pyRevit extensions folder"]
        REVIT["Revit loads on startup"]
    end

    subgraph Release["Release Build"]
        SCRIPT["build-release.ps1"]
        ZIP["package-release.ps1"]
        DIST["dist/HD.extension-vX.X.X.zip"]
    end

    SRC --> BUILD --> DLL --> COPY --> PYREVIT --> REVIT
    DLL --> JSON
    SRC --> SCRIPT --> ZIP --> DIST

    style Dev fill:#e3f2fd,stroke:#1565C0
    style Package fill:#e8f5e9,stroke:#2E7D32
    style Deploy fill:#fff3e0,stroke:#EF6C00
    style Release fill:#f3e5f5,stroke:#7B1FA2
```

---

## Full Project Structure

```
RevitCode/
├── src/                                # Source code (PRIVATE)
│   ├── HD.Core/                        # Shared library (helpers, services)
│   ├── RevitChat/                      # AI chatbot — skills, handler, learning
│   │   ├── Skills/                     # 27 skill classes, 170+ tools
│   │   │   ├── SkillRegistry.cs        # Tool registration & routing
│   │   │   ├── QuerySkill.cs           # get_elements, count, search
│   │   │   ├── ModifySkill.cs          # set_parameter, delete, move
│   │   │   ├── ExportSkill.cs          # CSV, JSON, PDF, IFC export
│   │   │   ├── ViewControlSkill.cs     # hide, isolate, color override
│   │   │   ├── MepSystemAnalysisSkill  # duct/pipe summary, pressure
│   │   │   ├── MepConnectivitySkill    # auto-connect, traverse network
│   │   │   ├── ClashDetectionSkill     # spatial clash checks
│   │   │   └── ... (20+ more)
│   │   ├── Handler/
│   │   │   └── RevitChatHandler.cs     # IExternalEventHandler
│   │   ├── Services/
│   │   │   ├── PromptAnalyzer.cs       # Intent/entity extraction
│   │   │   ├── ToolExecutionService.cs # Queue → ExternalEvent → Result
│   │   │   ├── SelfLearningOrchestrator.cs
│   │   │   ├── ContextualAutoTrainer.cs
│   │   │   ├── ChatBandit.cs           # RL (Q-learning)
│   │   │   ├── ChatToolClassifier.cs   # ANN (ONNX)
│   │   │   ├── EmbeddingMatcher.cs     # Semantic matching
│   │   │   └── ...
│   │   ├── Models/
│   │   └── ViewModel/
│   │       └── BaseChatViewModel.cs    # Core chat flow
│   │
│   ├── RevitChatLocal/                 # Local Ollama variant
│   │   ├── Services/
│   │   │   └── OllamaChatService.cs    # HTTP to Ollama + smart routing
│   │   ├── UI/
│   │   │   └── LocalChatWindow.xaml    # WPF chat window
│   │   └── ViewModel/
│   │       └── LocalChatViewModel.cs
│   │
│   ├── SmartTag/                       # AI auto-tagging tool
│   │   ├── Services/
│   │   │   ├── TagPlacementService.cs  # Scoring + collision avoidance
│   │   │   ├── RuleEngine.cs           # JSON rule loading
│   │   │   └── SpatialIndex.cs         # Grid-based collision queries
│   │   ├── ML/
│   │   │   ├── RLAgent.cs              # ONNX RL inference
│   │   │   └── ContextAnalyzer.cs      # Element context extraction
│   │   └── Data/                       # Rules, patterns, training data
│   │
│   ├── CommonFeature/                  # BIM utility tools
│   └── CheckCode/                      # Model quality checks
│
├── HD.extension/                       # pyRevit extension (runtime)
│   ├── lib/
│   │   ├── net8/                       # All compiled DLLs + dependencies
│   │   │   └── Data/
│   │   │       ├── ChatConfig/         # keyword_groups, fewshot_examples,
│   │   │       │                       # chat_normalization, tool_schema_hints
│   │   │       ├── Rules/Tagging/      # Tag placement rules (JSON)
│   │   │       ├── Patterns/           # Tag position patterns
│   │   │       ├── Models/             # ONNX classifiers, RL checkpoints
│   │   │       └── Feedback/           # Per-user learned data
│   │   └── launcher_base.py            # Shared Python launcher
│   └── HD.tab/
│       ├── AI.panel/                   # RevitChat, RevitChatLocal
│       ├── Labeling.panel/             # SmartTag
│       ├── General.panel/              # Extension, Settings, Reload
│       └── WIP.panel/                  # CommonFeature, CheckCode
│
├── docs/                               # Architecture & design docs
│   ├── RevitChat-Architecture.md       # Full chat system documentation
│   ├── MCP-ARCHITECTURE-PLAN.md        # MCP Server integration plan
│   ├── UPGRADE-ROADMAP.md              # 95-item feature roadmap
│   └── ...
│
├── build-release.ps1                   # Build without PDB
└── package-release.ps1                 # Create distribution ZIP
```

---

## Development

### Prerequisites
- .NET SDK 8.0+
- Revit 2025 or 2026
- pyRevit 4.8+
- Ollama (for local AI chat — `qwen2.5:7b` recommended)

### Build Commands

```powershell
# Build all projects for development
dotnet build src/HD.Core/HD.Core.csproj -c Release
dotnet build src/RevitChat/RevitChat.csproj -c Release
dotnet build src/RevitChatLocal/RevitChatLocal.csproj -c Release
dotnet build src/SmartTag/SmartTag.csproj -c Release
dotnet build src/CommonFeature/CommonFeature.csproj -c Release

# Build release (no PDB) and package
.\build-release.ps1 -Version "2.1.0" -Clean
.\package-release.ps1 -Version "2.1.0"
```

### Adding New Tools (Skills)

1. Create a new class implementing `IRevitSkill` in `src/RevitChat/Skills/`
2. Register in `SkillRegistry.CreateDefault()`
3. Build — the tool is automatically available in both Chat UI and future MCP

### Adding New pyRevit Buttons

1. Create pushbutton folder in `HD.extension/HD.tab/<Panel>.panel/`
2. Use `launcher_base.py` for the thin launcher script:

```python
from launcher_base import launch_dll
launch_dll(
    dll_name="YourTool.dll",
    namespace="YourTool",
    method="Run"
)
```

---

## Distribution

### For Users
1. Download the ZIP from `dist/` folder
2. Extract to: `%APPDATA%\pyRevit-Master\extensions\`
3. Reload pyRevit

### What's Included in Release
- Compiled DLLs only (no source code, no debug symbols)
- Python launcher scripts
- JSON config files (ChatConfig, Rules, Patterns)
- Icons

### What's NOT Included
- C# source code (.cs files)
- Debug symbols (.pdb files)
- Development files

---

## Documentation

| Document | Description |
|----------|-------------|
| [RevitChat Architecture](docs/RevitChat-Architecture.md) | Full AI chatbot system with Mermaid diagrams |
| [MCP Architecture Plan](docs/MCP-ARCHITECTURE-PLAN.md) | Model Context Protocol integration |
| [Upgrade Roadmap](docs/UPGRADE-ROADMAP.md) | 95-item feature roadmap (8 phases) |
| [Release Notes v2.1.0](docs/RELEASE-NOTES-v2.1.0.md) | Latest release changelog |
| [Code Optimization](docs/code-optimization-proposals.md) | Refactoring proposals & results |
| [Test Prompts](docs/TEST-PROMPTS-REVIT.md) | Chatbot test suite (EN/VN/mixed) |

---

## License

Proprietary — All rights reserved
