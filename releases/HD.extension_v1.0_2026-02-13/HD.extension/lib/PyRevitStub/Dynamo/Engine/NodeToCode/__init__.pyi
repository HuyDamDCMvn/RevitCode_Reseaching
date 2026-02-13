from typing import Tuple, Set, Iterable, List


class INamingProvider:
    def GetTypeDependentName(self, type: Type) -> str: ...
