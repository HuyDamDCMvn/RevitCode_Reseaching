from typing import Tuple, Set, Iterable, List


class Analytics:
    @property
    def DisableAnalytics() -> bool: ...
    @property
    def ReportingAnalytics() -> bool: ...
    def TrackStartupTime(productName: str, time: TimeSpan, description: str) -> None: ...
    def TrackEvent(action: Actions, category: Categories, description: str, value: Nullable) -> None: ...
    def TrackPreference(name: str, stringValue: str, metricValue: Nullable) -> None: ...
    def TrackTimedEvent(category: Categories, variable: str, time: TimeSpan, description: str) -> None: ...
    def TrackScreenView(viewName: str) -> None: ...
    def TrackActivityStatus(activityType: str) -> None: ...
    def TrackException(ex: Exception, isFatal: bool) -> None: ...
    def CreateTimedEvent(category: Categories, variable: str, description: str, value: Nullable) -> IDisposable: ...
    def CreateTaskTimedEvent(category: Categories, variable: str, description: str, value: Nullable) -> Task: ...
    def TrackCommandEvent(name: str, description: str, value: Nullable) -> IDisposable: ...
    def TrackTaskCommandEvent(name: str, description: str, value: Nullable, parameters: IDictionary) -> Task: ...
    def EndTaskCommandEvent(taskEvent: Task) -> None: ...
    def TrackFileOperationEvent(filepath: str, operation: Actions, size: int, description: str) -> IDisposable: ...
    def TrackTaskFileOperationEvent(filepath: str, operation: Actions, size: int, description: str) -> Task: ...


class Categories:
    ApplicationLifecycle = 0
    Stability = 1
    NodeOperations = 2
    Performance = 3
    Command = 4
    FileOperation = 5
    SearchUX = 6
    Preferences = 7
    PackageManager = 8
    Upgrade = 9
    Engine = 10
    NodeAutoCompleteOperations = 11
    InCanvasSearchOperations = 12
    PythonOperations = 13
    ExtensionOperations = 14
    ViewExtensionOperations = 15
    PackageManagerOperations = 16
    NoteOperations = 17
    WorkspaceReferencesOperations = 18
    WorkspaceReferences = 19
    GroupOperations = 20
    NodeContextMenuOperations = 21
    ConnectorOperations = 22
    GroupStyleOperations = 23
    GuidedTourOperations = 24
    SplashScreenOperations = 25
    DynamoMLDataPipelineOperations = 26
    DynamoHomeOperations = 27


class Actions:
    Start = 0
    End = 1
    Create = 2
    Delete = 3
    Move = 4
    Copy = 5
    Open = 6
    Close = 7
    Read = 8
    Write = 9
    Save = 10
    SaveAs = 11
    New = 12
    EngineFailure = 13
    Filter = 14
    Unresolved = 15
    Downloaded = 16
    Installed = 17
    Select = 18
    Migration = 19
    Switch = 20
    Run = 21
    Load = 22
    Dock = 23
    Undock = 24
    Rate = 25
    Pin = 26
    Unpin = 27
    DownloadNew = 28
    KeepOld = 29
    PackageReferences = 30
    LocalReferences = 31
    ExternalReferences = 32
    Ungroup = 33
    Expanded = 34
    Collapsed = 35
    AddedTo = 36
    RemovedFrom = 37
    Preview = 38
    Freeze = 39
    Rename = 40
    Show = 41
    Set = 42
    Dismiss = 43
    Undismiss = 44
    Break = 45
    Hide = 46
    BuiltInPackageConflict = 47
    Sort = 48
    View = 49
    ViewDocumentation = 50
    MissingDocumentation = 51
    Cancel = 52
    Completed = 53
    Next = 54
    Previous = 55
    TimeElapsed = 56
    SignIn = 57
    SignOut = 58
    Import = 59
    Export = 60


class HeartBeatType:
    User = 0
    Machine = 1


class IAnalyticsClient:
    @property
    def ReportingAnalytics(self) -> bool: ...
    def Start(self) -> None: ...
    def ShutDown(self) -> None: ...
    def TrackEvent(self, action: Actions, category: Categories, description: str, value: Nullable) -> None: ...
    def TrackPreference(self, name: str, stringValue: str, metricValue: Nullable) -> None: ...
    def TrackTimedEvent(self, category: Categories, variable: str, time: TimeSpan, description: str) -> None: ...
    def TrackScreenView(self, viewName: str) -> None: ...
    def TrackException(self, ex: Exception, isFatal: bool) -> None: ...
    def TrackActivityStatus(self, activityType: str) -> None: ...
    def CreateTimedEvent(self, category: Categories, variable: str, description: str, value: Nullable) -> IDisposable: ...
    def CreateTaskTimedEvent(self, category: Categories, variable: str, description: str, value: Nullable) -> Task: ...
    def CreateCommandEvent(self, name: str, description: str, value: Nullable) -> IDisposable: ...
    def CreateTaskCommandEvent(self, name: str, description: str, value: Nullable, parameters: IDictionary) -> Task: ...
    def EndEventTask(self, taskToEnd: Task) -> None: ...
    def TrackFileOperationEvent(self, filepath: str, operation: Actions, size: int, description: str) -> IDisposable: ...
    def TrackTaskFileOperationEvent(self, filepath: str, operation: Actions, size: int, description: str) -> Task: ...
