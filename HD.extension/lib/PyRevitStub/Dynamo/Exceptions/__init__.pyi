from typing import Tuple, Set, Iterable, List


class CustomNodePackageLoadException(LibraryLoadFailedException):
    def __init__(self, path: str, installedPath: str, reason: str): ...
    @property
    def InstalledPath(self) -> str: ...


class LibraryLoadFailedException:
    def __init__(self, path: str, reason: str): ...
