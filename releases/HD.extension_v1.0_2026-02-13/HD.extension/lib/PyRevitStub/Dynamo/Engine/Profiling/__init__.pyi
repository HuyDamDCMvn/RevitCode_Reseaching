from typing import Tuple, Set, Iterable, List


class IProfilingExecutionTimeData:
    @property
    def TotalExecutionTime(self) -> Nullable: ...
    def NodeExecutionTime(self, node: NodeModel) -> Nullable: ...
