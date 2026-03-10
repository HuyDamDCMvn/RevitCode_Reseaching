# RevitChat Architecture

## Overview

RevitChat is an AI chatbot integrated into Autodesk Revit, allowing users to query and manipulate BIM models through 170+ tools using Vietnamese, English, or mixed-language prompts. The system ships in two variants:

| | RevitChat | RevitChatLocal |
|---|---|---|
| Chat backend | OpenAI (cloud) | Ollama (local, qwen2.5:7b) |
| Window | `RevitChatWindow` | `LocalChatWindow` |
| ViewModel | `RevitChatViewModel` | `LocalChatViewModel` |
| Shared | Handler, Queue, Skills, Learning, BaseChatViewModel | same |

---

## High-Level Architecture

```mermaid
flowchart TB
    subgraph pyRevit["pyRevit Launcher"]
        PY["script.py<br/>(detect Revit version → load DLL)"]
    end

    subgraph Entry["Entry Point (Revit Main Thread)"]
        E1["Entry.ShowTool(UIApplication)"]
        INIT["Initialize Services:<br/>ChatFeedback, InteractionLogger,<br/>AdaptiveWeights, ChatBandit,<br/>ChatToolClassifier, SelfLearningOrchestrator,<br/>DynamicFewShotSelector, ContextualAutoTrainer"]
    end

    subgraph UI["WPF UI Layer (UI Thread)"]
        W["LocalChatWindow<br/>DataContext binding only"]
        INPUT["TextBox → SendCommand"]
        MSGS["Messages ListView"]
    end

    subgraph VM["ViewModel Layer (Background + Dispatcher)"]
        BVM["BaseChatViewModel<br/>SendAsync → ProcessToolCallLoop → RecordLearning"]
        LVM["LocalChatViewModel<br/>Settings, Model selection"]
    end

    subgraph Chat["Chat Service (Background Thread)"]
        OCS["OllamaChatService<br/>HTTP → localhost:11434"]
        PROMPT["BuildSystemPrompt<br/>+ BuildToolCatalog<br/>+ BuildDynamicExamples"]
        NORM["NormalizeForMatching<br/>(Vietnamese → English)"]
        RETRY["GetCompletionWithRetry<br/>+ Chinese detection"]
    end

    subgraph Learning["Self-Learning Pipeline (Background)"]
        SO["SelfLearningOrchestrator"]
        CAT["ContextualAutoTrainer"]
        DFS["DynamicFewShotSelector"]
        AWM["AdaptiveWeightManager"]
        CB["ChatBandit (RL)"]
        CTC["ChatToolClassifier (ONNX ANN)"]
        EM["EmbeddingMatcher (Semantic)"]
        CFS["ChatFeedbackService"]
    end

    subgraph RevitAPI["Revit API (Main Thread Only)"]
        EE["ExternalEvent"]
        RH["RevitChatHandler<br/>IExternalEventHandler"]
        Q["ChatRequestQueue"]
        SR["SkillRegistry"]
    end

    subgraph Skills["Skills (170+ tools)"]
        S1["QuerySkill"]
        S2["MepSystemAnalysisSkill"]
        S3["ViewControlSkill"]
        S4["ModifySkill"]
        S5["ExportSkill"]
        S6["MepConnectivitySkill"]
        S7["ModelHealthSkill"]
        S8["DimensionTagSkill"]
        S9["... 25+ skills"]
    end

    PY --> E1
    E1 --> INIT
    E1 --> W
    W --> BVM
    BVM --> OCS
    OCS --> PROMPT
    OCS --> NORM
    OCS --> RETRY
    BVM --> Q
    BVM --> EE
    EE --> RH
    RH --> Q
    RH --> SR
    SR --> Skills
    BVM --> SO
    SO --> CAT
    SO --> DFS
    SO --> AWM
    SO --> CB
    OCS --> CTC
    OCS --> EM
    BVM --> CFS

    style pyRevit fill:#f9f,stroke:#333
    style RevitAPI fill:#fdd,stroke:#c00
    style Learning fill:#dfd,stroke:#0a0
    style Chat fill:#ddf,stroke:#00a
```

---

## Main Message Flow

```mermaid
sequenceDiagram
    actor User
    participant UI as LocalChatWindow
    participant VM as BaseChatViewModel
    participant PA as PromptAnalyzer
    participant CS as OllamaChatService
    participant Ollama as Ollama Server
    participant TES as ToolExecutionService
    participant Q as ChatRequestQueue
    participant EE as ExternalEvent
    participant RH as RevitChatHandler
    participant SR as SkillRegistry
    participant Learn as Learning Pipeline

    User->>UI: Types "count all ducts"
    UI->>VM: SendCommand → SendAsync()

    Note over VM: Phase 1: Analyze prompt
    VM->>PA: Analyze("count all ducts")
    PA-->>VM: Intent=Count, Category=Ducts

    Note over VM: Phase 2: Send to LLM
    VM->>CS: SendMessageAsync("count all ducts")
    CS->>CS: NormalizeForMatching()
    CS->>CS: BuildToolCatalog (smart mode)
    CS->>CS: BuildSystemPrompt + DynamicExamples
    CS->>Ollama: POST /v1/chat/completions
    Ollama-->>CS: <tool_call> count_elements {category: "Ducts"}
    CS-->>VM: (response, toolCalls)

    Note over VM: Phase 3: Execute tool on Revit thread
    VM->>TES: ExecuteAsync(toolCalls)
    TES->>Q: Enqueue(count_elements)
    TES->>EE: Raise()
    Note over EE,RH: Revit schedules Execute()
    EE->>RH: Execute(UIApplication)
    RH->>Q: TryDequeue()
    RH->>SR: ExecuteTool("count_elements", args)
    SR-->>RH: "{count: 1053, category: Ducts}"
    RH-->>TES: OnToolCallsCompleted(results)
    TES-->>VM: results

    Note over VM: Phase 4: Summarize results
    VM->>CS: ContinueWithToolResultsAsync(results)
    CS->>Ollama: "Answer based on results..."
    Ollama-->>CS: "There are 1,053 ducts in the model."
    CS-->>VM: final response

    Note over VM: Phase 5: Learn from interaction
    VM->>UI: Display response
    VM->>Learn: RecordLearningSignals(success)
    Learn->>Learn: Update Bandit, Weights, FewShots, Embeddings
```

---

## Smart Tool Selection Pipeline

```mermaid
flowchart LR
    subgraph Input
        MSG["User Message<br/>'classify by size'"]
        PREV["Previous Message<br/>'count ducts'"]
    end

    subgraph Normalize
        NORM["NormalizeForMatching<br/>chat_normalization.json<br/>+ StripDiacritics<br/>+ Learned normalizations"]
    end

    subgraph Selection["Tool Selection (6 sources)"]
        CORE["1. Core Tools<br/>(always included)"]
        KW["2. Keyword Groups<br/>keyword_groups.json"]
        PA["3. PromptAnalyzer<br/>SuggestedTools"]
        ANN["4. ANN Classifier<br/>tool_classifier.onnx"]
        FUP["5. Follow-up Patterns<br/>ContextualAutoTrainer"]
        EMB["6. Embedding Recs<br/>EmbeddingMatcher"]
    end

    subgraph Catalog["Tool Catalog"]
        TOP["Top 25 tools<br/>(15 for CPU mode)"]
    end

    subgraph Examples["Few-Shot Examples (4 sources)"]
        STATIC["1. fewshot_examples.json"]
        APPROVED["2. ChatFeedbackService<br/>approved examples"]
        DYNAMIC["3. DynamicFewShotSelector<br/>learned from success"]
        CONTEXT["4. ContextualAutoTrainer<br/>from resolved failures"]
    end

    subgraph Prompt["System Prompt"]
        SYS["Rules + Domain terms<br/>+ Tool catalog<br/>+ Examples<br/>+ Context hints"]
    end

    MSG --> NORM
    PREV --> NORM
    NORM --> Selection
    Selection --> TOP
    MSG --> Examples
    TOP --> SYS
    Examples --> SYS
    SYS --> LLM["→ Ollama LLM"]

    style Selection fill:#e8f4fd,stroke:#2196F3
    style Examples fill:#e8fde8,stroke:#4CAF50
```

---

## Self-Learning Pipeline

```mermaid
flowchart TB
    subgraph Signals["Learning Signals"]
        S1["Tool call succeeded"]
        S2["Fallback / no tool call"]
        S3["Chinese language detected"]
        S4["Thumbs Up"]
        S5["Thumbs Down"]
        S6["User correction (retry)"]
    end

    subgraph Record["Recording Layer"]
        R1["SelfLearningOrchestrator<br/>.RecordInteraction()"]
        R2["ContextualAutoTrainer<br/>.RecordFallback()"]
        R3["ContextualAutoTrainer<br/>.RecordLanguageIssue()"]
        R4["ChatFeedbackService<br/>.SaveApproved()"]
        R5["ChatFeedbackService<br/>.SaveCorrection()"]
        R6["ContextualAutoTrainer<br/>.RecordCorrection()"]
    end

    subgraph Update["Update Services"]
        U1["ChatBandit<br/>Q-table update (RL)"]
        U2["AdaptiveWeightManager<br/>keyword → intent weights"]
        U3["DynamicFewShotSelector<br/>prompt → tool examples"]
        U4["EmbeddingMatcher<br/>semantic vectors"]
        U5["ContextualAutoTrainer<br/>failure → success mapping"]
    end

    subgraph AutoImprove["Auto-Improvement (Threshold Triggers)"]
        A1["AutoAugment<br/>Every 10 interactions:<br/>Generate variants (typo, no-diacritics)"]
        A2["Consolidate<br/>Every 25 interactions:<br/>Promote weak → strong patterns"]
        A3["PrepareRetrain<br/>Every 100 interactions:<br/>Export training batch for ANN"]
    end

    subgraph Persist["Persisted Data (per-user JSON)"]
        P1["learning_stats_{user}.json"]
        P2["contextual_learned_{user}.json"]
        P3["learned_examples_{user}.json"]
        P4["adaptive_weights_{user}.json"]
        P5["bandit_qtable_{user}.json"]
        P6["embeddings_{user}.json"]
        P7["chat_feedback_{user}.json"]
    end

    S1 --> R1
    S2 --> R2
    S3 --> R3
    S4 --> R4
    S5 --> R5
    S6 --> R6

    R1 --> U1
    R1 --> U2
    R1 --> U3
    R1 --> U4
    R2 --> U5
    R6 --> U5

    R1 --> AutoImprove
    Update --> Persist

    style Signals fill:#fff3e0,stroke:#FF9800
    style AutoImprove fill:#e8f5e9,stroke:#4CAF50
    style Persist fill:#f3e5f5,stroke:#9C27B0
```

---

## Thread Model

```mermaid
flowchart LR
    subgraph RevitMain["Revit Main Thread"]
        direction TB
        E["Entry.ShowTool()"]
        RH["RevitChatHandler.Execute()"]
        SK["SkillRegistry.ExecuteTool()"]
        E --> RH
        RH --> SK
    end

    subgraph UIThread["WPF UI Thread"]
        direction TB
        W["LocalChatWindow"]
        DISP["Dispatcher.BeginInvoke<br/>(update Messages, Status)"]
    end

    subgraph Background["Background Threads"]
        direction TB
        VM["BaseChatViewModel.SendAsync()"]
        CS["OllamaChatService (HTTP)"]
        LEARN["Learning services (lock-protected)"]
        EMB["EmbeddingMatcher (HTTP to Ollama)"]
    end

    Background -->|ExternalEvent.Raise| RevitMain
    RevitMain -->|OnToolCallsCompleted| Background
    Background -->|Dispatcher.BeginInvoke| UIThread

    style RevitMain fill:#ffcdd2,stroke:#f44336
    style UIThread fill:#fff9c4,stroke:#fbc02d
    style Background fill:#c8e6c9,stroke:#4caf50
```

**Invariant rules:**
- ViewModel **NEVER** calls Revit API directly
- Only `RevitChatHandler.Execute()` may call Revit API
- All Revit actions go through: `Request → Queue → ExternalEvent.Raise() → Handler`
- AI/HTTP calls run on background threads, **NEVER** inside ExternalEvent handler

---

## Skill Packs & Tools

```mermaid
mindmap
  root((SkillRegistry<br/>170+ tools))
    Core
      QuerySkill
        get_elements
        count_elements
        search_elements
        get_element_parameters
      ProjectInfoSkill
        get_project_info
        get_levels
        get_categories
        get_rooms
      ModifySkill
        set_parameter_value
        delete_elements
        move_elements
        batch_rename_pattern
      ExportSkill
        export_to_csv
        export_to_json
        export_pdf
        export_ifc
    ViewControl
      ViewControlSkill
        hide/unhide_elements
        isolate_elements
        override_color_by_*
        create_3d_view
        get_current_selection
      SelectionFilterSkill
        select_by_parameter_value
        select_by_bounding_box
    MEP
      MepSystemAnalysis
        get_duct_summary
        get_pipe_summary
        calculate_system_totals
      MepConnectivity
        auto_connect_mep
        get_connector_info
        traverse_mep_network
      MepQuantityTakeoff
        mep_quantity_takeoff
        export_mep_boq
      MepValidation
        check_disconnected
        check_slope
        check_insulation
      MepFitting
        create_elbow
        create_tap_connection
        bloom_connectors
    BIMCoordinator
      ModelHealth
        audit_model_standards
        get_model_warnings
      ClashDetection
        detect_clashes
      NamingAudit
        audit_naming_conventions
    Modeler
      DimensionTag
        tag_elements
        tag_all_in_view
      SheetManagement
        create_sheet
        place_view_on_sheet
      RoomArea
        get_room_boundaries
```

---

## Bilingual Processing Flow

```mermaid
flowchart TD
    INPUT["User: 'count ducts on Level 1'<br/>or: 'dem ong gio tren Level 1'"]

    subgraph Normalize["Normalization"]
        N1["ToLowerInvariant"]
        N2["chat_normalization.json<br/>'ong gio' → 'duct'"]
        N3["StripVietnameseDiacritics<br/>'dem' → 'dem'"]
        N4["Learned normalizations<br/>(ContextualAutoTrainer)"]
    end

    subgraph Analyze["PromptAnalyzer"]
        PA1["Intent: Count"]
        PA2["Category: Ducts"]
        PA3["Level: Level 1"]
        PA4["SuggestedTools: count_elements"]
    end

    subgraph SystemPrompt["System Prompt Injection"]
        SP1["Rule 8: Vietnamese domain terms mapping"]
        SP2["Rule 7: NEVER reply in Chinese"]
        SP3["Rule 12: Follow-up context"]
        SP4["Rule 13: Action follow-ups"]
    end

    subgraph FewShot["Dynamic Few-Shot"]
        FS1["Static: matching keyword examples"]
        FS2["Learned: similar past successes"]
        FS3["Contextual: resolved failures"]
    end

    INPUT --> Normalize --> Analyze
    Analyze --> SystemPrompt
    Analyze --> FewShot
    SystemPrompt --> LLM["Ollama qwen2.5:7b"]
    FewShot --> LLM
    LLM --> OUTPUT["<tool_call><br/>count_elements {category: 'Ducts', level: 'Level 1'}"]

    style Normalize fill:#e3f2fd,stroke:#1976D2
    style Analyze fill:#f3e5f5,stroke:#7B1FA2
```

---

## File Structure

```
src/
├── RevitChat/                          # Shared library (DLL)
│   ├── Entry.cs                        # Entry point (OpenAI version)
│   ├── Handler/
│   │   └── RevitChatHandler.cs         # IExternalEventHandler (Revit thread)
│   ├── Models/
│   │   ├── IChatService.cs             # Chat service interface
│   │   ├── ChatMessage.cs              # UI message model
│   │   ├── ToolCallRequest.cs          # Tool call DTO
│   │   └── WorkingMemory.cs            # Session memory
│   ├── Services/
│   │   ├── PromptAnalyzer.cs           # Intent/entity extraction
│   │   ├── ToolExecutionService.cs     # Queue → ExternalEvent → Results
│   │   ├── ChatFeedbackService.cs      # Thumbs up/down persistence
│   │   ├── AdaptiveWeightManager.cs    # Keyword weight learning
│   │   ├── ChatBandit.cs               # RL tool selection (Q-learning)
│   │   ├── ChatToolClassifier.cs       # ONNX ANN classifier
│   │   ├── EmbeddingMatcher.cs         # Semantic embedding matching
│   │   ├── DynamicFewShotSelector.cs   # Learned few-shot examples
│   │   ├── SelfLearningOrchestrator.cs # Meta-learning coordinator
│   │   ├── ContextualAutoTrainer.cs    # Failure/follow-up learning
│   │   ├── InteractionLogger.cs        # Raw interaction logging
│   │   └── SelfTrainingService.cs      # Offline training data export
│   ├── Skills/                         # 25+ skill classes, 170+ tools
│   │   ├── SkillRegistry.cs            # Tool registration & routing
│   │   ├── QuerySkill.cs
│   │   ├── MepSystemAnalysisSkill.cs
│   │   ├── ViewControlSkill.cs
│   │   └── ...
│   └── ViewModel/
│       └── BaseChatViewModel.cs        # Core send/process/learn flow
│
├── RevitChatLocal/                     # Local Ollama variant (DLL)
│   ├── Entry.cs                        # Entry point (Ollama version)
│   ├── Services/
│   │   └── OllamaChatService.cs        # Ollama HTTP client + smart routing
│   ├── UI/
│   │   ├── LocalChatWindow.xaml        # WPF chat UI
│   │   └── LocalChatWindow.xaml.cs     # Code-behind (binding only)
│   └── ViewModel/
│       └── LocalChatViewModel.cs       # Settings, model selection
│
HD.extension/lib/net8/
├── Data/
│   ├── ChatConfig/
│   │   ├── keyword_groups.json         # Keyword → tool group mapping
│   │   ├── fewshot_examples.json       # Static few-shot examples
│   │   ├── chat_normalization.json     # Vietnamese → English normalization
│   │   └── tool_schema_hints.json      # Compact tool signatures
│   ├── Feedback/                       # Per-user learned data
│   │   ├── contextual_learned_{user}.json
│   │   ├── learned_examples_{user}.json
│   │   ├── adaptive_weights_{user}.json
│   │   ├── bandit_qtable_{user}.json
│   │   └── embeddings_{user}.json
│   └── Models/
│       ├── tool_classifier.onnx        # ANN tool classifier
│       └── tool_classifier_index.json  # Tool name ↔ index mapping
```
