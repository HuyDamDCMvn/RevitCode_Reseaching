"""CLR (Common Language Runtime) stubs for IronPython/.NET interop."""
from typing import Any, Optional

def AddReference(assembly_name: str) -> None:
    """Add a reference to a .NET assembly by name."""
    ...

def AddReferenceToFile(file_path: str) -> None:
    """Add a reference to a .NET assembly by file path."""
    ...

def AddReferenceToFileAndPath(file_path: str) -> None:
    """Add a reference to a .NET assembly by file path and add its directory to the search path."""
    ...

def AddReferenceByName(assembly_name: str) -> None:
    """Add a reference to a .NET assembly by its full name."""
    ...

def AddReferenceByPartialName(assembly_name: str) -> None:
    """Add a reference to a .NET assembly by partial name."""
    ...

def GetClrType(type_obj: type) -> Any:
    """Get the CLR Type object for a Python type."""
    ...

def ImportExtensions(namespace: Any) -> None:
    """Import extension methods from a namespace."""
    ...

def Convert(obj: Any, target_type: type) -> Any:
    """Convert an object to a specific CLR type."""
    ...

def SetCommandDispatcher(dispatcher: Any) -> None:
    """Set the command dispatcher."""
    ...

def Reference(assembly: Any) -> Any:
    """Create a reference to an assembly."""
    ...
