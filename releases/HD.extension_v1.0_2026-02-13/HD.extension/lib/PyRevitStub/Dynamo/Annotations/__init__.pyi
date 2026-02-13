from typing import Tuple, Set, Iterable, List


class NotifyPropertyChangedInvocatorAttribute:
    @overload
    def __init__(self): ...
    @overload
    def __init__(self, parameterName: str): ...
    @property
    def ParameterName(self) -> str: ...
