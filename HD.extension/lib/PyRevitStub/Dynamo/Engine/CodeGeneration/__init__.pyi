from typing import Tuple, Set, Iterable, List


class AstBuilder(LogSourceBase):
    def CompileToAstNodes(self, nodes: Iterable[NodeModel], context: CompilationContext, verboseLogging: bool) -> Iterable[Tuple]: ...


class CompilationContext:
    #None = 0
    DeltaExecution = 1
    NodeToCode = 2
    PreviewGraph = 3


class CompiledEventArgs:
    @property
    def NodeId(self) -> Guid: ...
    @property
    def AstNodes(self) -> Iterable[AssociativeNode]: ...


class CompilingEventArgs:
    def __init__(self, node: Guid): ...
    @property
    def NodeId(self) -> Guid: ...


class IAstNodeContainer:
    def OnCompiling(self, nodeGuid: Guid) -> None: ...
    def OnCompiled(self, nodeGuid: Guid, astNodes: Iterable[AssociativeNode]) -> None: ...
