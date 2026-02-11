---
name: api-diff-compat-shim
description: Create compatibility wrappers for Revit API differences across versions 2023-2026. Use when Revit API behavior or signatures differ between versions, when targeting net48 and net8.0-windows simultaneously, or when isolating version-specific code from business logic.
---

# API Diff Compat Shim (C# + Python)

Create version-agnostic wrappers to isolate Revit API differences, keeping business logic clean and testable.

## When to Use

- Revit API method signatures changed between 2023-2026
- Behavior differs (returns null vs throws, enum values changed)
- Class/namespace moved or renamed
- Deprecated API with recommended replacement
- Need to target both `net48` (2023-2024) and `net8.0-windows` (2025-2026)

## Required Inputs

| Input | Example | Required |
|-------|---------|----------|
| API difference | `Element.Name` vs `Element.get_Name()` | Yes |
| Affected versions | 2023-2024 vs 2025-2026 | Yes |
| Impacted surface area | Which classes/methods use it | Yes |
| Wrapper location | `Compat` namespace or `lib/compat.py` | Yes |

## Step 1: Identify Minimal Impacted Surface Area

Search for all usages of the affected API:

```bash
# Find all usages in C# code
rg "Element\.Name" --type cs

# Find all usages in Python code  
rg "element\.Name" --type py --glob "*.py"
```

Document findings:

```
Impacted files:
- src/Services/ElementService.cs (3 usages)
- src/Handlers/SelectionHandler.cs (1 usage)
- lib/utils.py (2 usages)
```

## Step 2: Create Compat Wrapper (C#)

### File Location

Place compat wrappers in a dedicated namespace:

```
src/
  Compat/
    ElementCompat.cs      # Element-related compat
    DocumentCompat.cs     # Document-related compat
    RevitVersionInfo.cs   # Version detection
```

### Version Detection Helper

```csharp
namespace {{Namespace}}.Compat
{
    /// <summary>
    /// Detects Revit version at runtime for conditional logic.
    /// </summary>
    public static class RevitVersionInfo
    {
        private static int? _majorVersion;

        public static int MajorVersion
        {
            get
            {
                if (!_majorVersion.HasValue)
                {
                    var assembly = typeof(Autodesk.Revit.DB.Document).Assembly;
                    var version = assembly.GetName().Version;
                    _majorVersion = version.Major;
                }
                return _majorVersion.Value;
            }
        }

        public static bool Is2025OrNewer => MajorVersion >= 2025;
        public static bool Is2024OrOlder => MajorVersion <= 2024;
    }
}
```

### Conditional Compilation Pattern (Preferred)

Use when you have **separate builds** for net48 vs net8:

```csharp
namespace {{Namespace}}.Compat
{
    public static class ElementCompat
    {
        /// <summary>
        /// Gets element name, handling API differences across versions.
        /// </summary>
        public static string GetName(Element element)
        {
            if (element == null) return string.Empty;

#if REVIT2025_OR_NEWER
            return element.Name;
#else
            return element.get_Name();
#endif
        }

        /// <summary>
        /// Sets element name where allowed.
        /// </summary>
        public static void SetName(Element element, string name)
        {
            if (element == null || string.IsNullOrEmpty(name)) return;

#if REVIT2025_OR_NEWER
            element.Name = name;
#else
            element.set_Name(name);
#endif
        }
    }
}
```

**Define symbols in .csproj:**

```xml
<PropertyGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
  <DefineConstants>$(DefineConstants);REVIT2025_OR_NEWER</DefineConstants>
</PropertyGroup>
```

### Runtime Detection Pattern (Alternative)

Use when you have a **single build** or need runtime flexibility:

```csharp
namespace {{Namespace}}.Compat
{
    public static class ElementCompat
    {
        /// <summary>
        /// Gets element name using reflection for version compatibility.
        /// Cache the method for performance.
        /// </summary>
        private static Func<Element, string> _getName;

        public static string GetName(Element element)
        {
            if (element == null) return string.Empty;

            if (_getName == null)
            {
                _getName = CreateGetNameFunc();
            }
            return _getName(element);
        }

        private static Func<Element, string> CreateGetNameFunc()
        {
            // Try property first (2025+)
            var prop = typeof(Element).GetProperty("Name");
            if (prop != null)
            {
                return e => (string)prop.GetValue(e);
            }

            // Fall back to method (2023-2024)
            var method = typeof(Element).GetMethod("get_Name");
            if (method != null)
            {
                return e => (string)method.Invoke(e, null);
            }

            return _ => string.Empty;
        }
    }
}
```

### Common API Differences Table

| API | 2023-2024 (net48) | 2025-2026 (net8) | Compat Wrapper |
|-----|-------------------|------------------|----------------|
| Element name | `get_Name()` / `set_Name()` | `Name` property | `ElementCompat.GetName()` |
| Family symbol | `FamilySymbol` | `FamilySymbol` (unchanged) | N/A |
| Filter rule | `FilterStringRule` ctor | `FilterStringRule.Create()` | `FilterCompat.CreateStringRule()` |
| UV Point | `UV` class | `UV` class | Check for behavior changes |

## Step 3: Create Compat Wrapper (Python)

For pyRevit scripts that need version compat:

### File Location

```
<Ext>.extension/
  lib/
    compat.py           # Python compat utilities
    net48/              # C# DLLs for 2023-2024
    net8/               # C# DLLs for 2025-2026
```

### Python Compat Module

```python
# lib/compat.py
"""
Revit API compatibility layer for pyRevit scripts.
Import this instead of using version-specific APIs directly.
"""

from pyrevit import HOST_APP

REVIT_VERSION = int(HOST_APP.version)
IS_2025_OR_NEWER = REVIT_VERSION >= 2025
IS_2024_OR_OLDER = REVIT_VERSION <= 2024


def get_element_name(element):
    """Get element name across Revit versions."""
    if element is None:
        return ""
    
    if IS_2025_OR_NEWER:
        return element.Name
    else:
        return element.get_Name()


def set_element_name(element, name):
    """Set element name across Revit versions."""
    if element is None or not name:
        return
    
    if IS_2025_OR_NEWER:
        element.Name = name
    else:
        element.set_Name(name)


def create_filter_string_rule(param_id, evaluator, value, case_sensitive=True):
    """Create FilterStringRule across Revit versions."""
    from Autodesk.Revit.DB import FilterStringRule
    
    if IS_2025_OR_NEWER:
        return FilterStringRule.Create(param_id, evaluator, value, case_sensitive)
    else:
        return FilterStringRule(param_id, evaluator, value, case_sensitive)
```

### Usage in pyRevit Scripts

```python
# script.py
from compat import get_element_name, IS_2025_OR_NEWER

# Use compat function instead of direct API
name = get_element_name(element)

# Or check version for more complex logic
if IS_2025_OR_NEWER:
    # Use new API features
    pass
else:
    # Use legacy approach
    pass
```

## Step 4: Update Callers to Use Wrapper

### Before (Version-Specific)

```csharp
// ❌ Direct API call - breaks across versions
public void RenameElement(Element element, string newName)
{
    element.set_Name(newName);  // Only works in 2023-2024
}
```

### After (Version-Agnostic)

```csharp
// ✅ Using compat wrapper - works in all versions
using {{Namespace}}.Compat;

public void RenameElement(Element element, string newName)
{
    ElementCompat.SetName(element, newName);
}
```

### Search and Replace Pattern

```bash
# Find all direct usages
rg "\.get_Name\(\)" --type cs

# Replace with compat wrapper (manual review recommended)
# element.get_Name() → ElementCompat.GetName(element)
```

## Step 5: Add Smoke Checks Per Version

### C# Unit Test Pattern

```csharp
namespace {{Namespace}}.Tests
{
    [TestFixture]
    public class CompatTests
    {
        [Test]
        public void ElementCompat_GetName_ReturnsName()
        {
            // Arrange - requires Revit context
            var element = GetTestElement();
            
            // Act
            var name = ElementCompat.GetName(element);
            
            // Assert
            Assert.IsNotNull(name);
            Assert.IsNotEmpty(name);
        }

        [Test]
        public void RevitVersionInfo_DetectsVersion()
        {
            // Should match actual Revit version
            var version = RevitVersionInfo.MajorVersion;
            Assert.That(version, Is.InRange(2023, 2026));
        }
    }
}
```

### Python Smoke Check Script

```python
# scripts/smoke_check_compat.py
"""
Run inside Revit to verify compat layer works.
"""
from pyrevit import script, forms, HOST_APP
import sys

# Add lib to path
sys.path.insert(0, script.get_bundle_file('lib'))
from compat import (
    REVIT_VERSION, 
    IS_2025_OR_NEWER,
    get_element_name
)

def run_checks():
    results = []
    
    # Check 1: Version detection
    results.append(f"Revit Version: {REVIT_VERSION}")
    results.append(f"Is 2025+: {IS_2025_OR_NEWER}")
    
    # Check 2: Element name compat
    from Autodesk.Revit.DB import FilteredElementCollector, Wall
    doc = __revit__.ActiveUIDocument.Document
    
    walls = FilteredElementCollector(doc).OfClass(Wall).FirstElement()
    if walls:
        name = get_element_name(walls)
        results.append(f"Wall name via compat: {name}")
    else:
        results.append("No walls found to test")
    
    return "\n".join(results)

if __name__ == "__main__":
    output = run_checks()
    forms.alert(output, title="Compat Smoke Check")
```

## Output Checklist

```
Diffs Isolated:
- [ ] All version-specific API calls wrapped in Compat class/module
- [ ] Wrapper methods have clear documentation
- [ ] Version detection cached (not repeated per call)
- [ ] Conditional compilation symbols defined in .csproj

Business Logic Clean:
- [ ] No #if directives in business logic classes
- [ ] No version checks scattered through codebase
- [ ] Callers use compat wrappers, not direct API
- [ ] Import compat module in Python scripts, not version checks inline

Tested:
- [ ] Smoke test passes in Revit 2023
- [ ] Smoke test passes in Revit 2024
- [ ] Smoke test passes in Revit 2025
- [ ] Smoke test passes in Revit 2026

Documented:
- [ ] API differences table updated
- [ ] Compat method has XML doc or docstring
- [ ] Breaking changes noted in changelog/readme
```

## Common Compat Patterns

### Null vs Exception Handling

```csharp
public static class DocumentCompat
{
    /// <summary>
    /// Gets active view, handling null safely across versions.
    /// Some versions throw, others return null.
    /// </summary>
    public static View GetActiveViewSafe(Document doc)
    {
        try
        {
            return doc.ActiveView;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return null;
        }
    }
}
```

### Enum Value Changes

```csharp
public static class EnumCompat
{
    /// <summary>
    /// Get BuiltInCategory value that may have been renamed/moved.
    /// </summary>
    public static BuiltInCategory GetStructuralFramingCategory()
    {
#if REVIT2025_OR_NEWER
        return BuiltInCategory.OST_StructuralFraming;
#else
        return BuiltInCategory.OST_StructuralFraming; // Same, but verify
#endif
    }
}
```

### Constructor vs Factory Method

```csharp
public static class FilterCompat
{
    public static FilterStringRule CreateStringRule(
        ElementId paramId,
        FilterStringRuleEvaluator evaluator,
        string value)
    {
#if REVIT2025_OR_NEWER
        return FilterStringRule.Create(paramId, evaluator, value);
#else
        return new FilterStringRule(paramId, evaluator, value, true);
#endif
    }
}
```

## File Organization

```
src/
  Compat/
    RevitVersionInfo.cs     # Version detection
    ElementCompat.cs        # Element API wrappers
    DocumentCompat.cs       # Document API wrappers
    FilterCompat.cs         # Filter API wrappers
    TransactionCompat.cs    # Transaction API wrappers
  Services/
    ElementService.cs       # Uses Compat wrappers
  Tests/
    CompatTests.cs          # Smoke tests

<Ext>.extension/
  lib/
    compat.py               # Python compat layer
  scripts/
    smoke_check_compat.py   # Python smoke test
```
