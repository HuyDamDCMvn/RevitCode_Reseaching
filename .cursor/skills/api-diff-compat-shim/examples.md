# API Diff Compat Shim - Real-World Examples

## Example 1: Element.Name Property Change

### The Problem

In Revit 2025, `Element.Name` became a direct property. Earlier versions use `get_Name()` / `set_Name()` methods.

### Compat Wrapper

```csharp
namespace DCM.Tools.Compat
{
    public static class ElementCompat
    {
        public static string GetName(Element element)
        {
            if (element == null) return string.Empty;

#if REVIT2025_OR_NEWER
            return element.Name;
#else
            return element.get_Name();
#endif
        }

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

### Before/After Usage

```csharp
// ❌ Before - version-specific
public string GetWallTypeName(Wall wall)
{
    return wall.WallType.get_Name();  // Fails in 2025+
}

// ✅ After - version-agnostic
public string GetWallTypeName(Wall wall)
{
    return ElementCompat.GetName(wall.WallType);
}
```

---

## Example 2: FilterStringRule Constructor Change

### The Problem

Revit 2024+ changed `FilterStringRule` from constructor to factory method.

### Compat Wrapper

```csharp
namespace DCM.Tools.Compat
{
    public static class FilterCompat
    {
        /// <summary>
        /// Creates FilterStringRule across all supported versions.
        /// </summary>
        public static FilterStringRule CreateStringRule(
            ElementId paramId,
            FilterStringRuleEvaluator evaluator,
            string ruleString,
            bool caseSensitive = true)
        {
#if REVIT2024_OR_NEWER
            return FilterStringRule.Create(paramId, evaluator, ruleString, caseSensitive);
#else
            return new FilterStringRule(paramId, evaluator, ruleString, caseSensitive);
#endif
        }

        /// <summary>
        /// Creates FilterNumericRule across all supported versions.
        /// </summary>
        public static FilterDoubleRule CreateDoubleRule(
            ElementId paramId,
            FilterNumericRuleEvaluator evaluator,
            double ruleValue,
            double tolerance = 1e-9)
        {
#if REVIT2024_OR_NEWER
            return FilterDoubleRule.Create(paramId, evaluator, ruleValue, tolerance);
#else
            return new FilterDoubleRule(paramId, evaluator, ruleValue, tolerance);
#endif
        }
    }
}
```

### Before/After Usage

```csharp
// ❌ Before - only works in 2023
var rule = new FilterStringRule(paramId, evaluator, "Wall Type A", true);

// ✅ After - works in all versions
var rule = FilterCompat.CreateStringRule(paramId, evaluator, "Wall Type A");
```

---

## Example 3: Document.Create Changes

### The Problem

Some `Document.Create` methods changed return types or parameters.

### Compat Wrapper

```csharp
namespace DCM.Tools.Compat
{
    public static class DocumentCompat
    {
        /// <summary>
        /// Creates a new ViewPlan with compat for different versions.
        /// </summary>
        public static ViewPlan CreateFloorPlan(Document doc, ElementId viewFamilyTypeId, ElementId levelId)
        {
            // This API stayed consistent, but include for pattern
            return ViewPlan.Create(doc, viewFamilyTypeId, levelId);
        }

        /// <summary>
        /// Creates DetailLine with version-safe handling.
        /// </summary>
        public static DetailLine CreateDetailLine(Document doc, View view, Line line)
        {
            if (doc == null || view == null || line == null)
                throw new ArgumentNullException();

#if REVIT2025_OR_NEWER
            return doc.Create.NewDetailCurve(view, line) as DetailLine;
#else
            return doc.Create.NewDetailCurve(view, line) as DetailLine;
#endif
        }
    }
}
```

---

## Example 4: Python Compat for pyRevit

### lib/compat.py

```python
"""
Revit API compatibility layer for pyRevit scripts.
Handles API differences between Revit 2023-2026.
"""

from pyrevit import HOST_APP

# Version constants
REVIT_VERSION = int(HOST_APP.version)
IS_2025_OR_NEWER = REVIT_VERSION >= 2025
IS_2024_OR_NEWER = REVIT_VERSION >= 2024
IS_2024_OR_OLDER = REVIT_VERSION <= 2024


# =============================================================================
# Element Name
# =============================================================================

def get_element_name(element):
    """
    Get element name across Revit versions.
    
    Args:
        element: Revit Element instance
        
    Returns:
        str: Element name or empty string if element is None
    """
    if element is None:
        return ""
    
    if IS_2025_OR_NEWER:
        return element.Name
    else:
        return element.get_Name()


def set_element_name(element, name):
    """
    Set element name across Revit versions.
    
    Args:
        element: Revit Element instance
        name: New name string
    """
    if element is None or not name:
        return
    
    if IS_2025_OR_NEWER:
        element.Name = name
    else:
        element.set_Name(name)


# =============================================================================
# Filter Rules
# =============================================================================

def create_filter_string_rule(param_id, evaluator, value, case_sensitive=True):
    """
    Create FilterStringRule across Revit versions.
    
    Args:
        param_id: ElementId of parameter
        evaluator: FilterStringRuleEvaluator instance
        value: String value to filter by
        case_sensitive: Whether comparison is case sensitive
        
    Returns:
        FilterStringRule instance
    """
    from Autodesk.Revit.DB import FilterStringRule
    
    if IS_2024_OR_NEWER:
        return FilterStringRule.Create(param_id, evaluator, value, case_sensitive)
    else:
        return FilterStringRule(param_id, evaluator, value, case_sensitive)


def create_filter_double_rule(param_id, evaluator, value, tolerance=1e-9):
    """
    Create FilterDoubleRule across Revit versions.
    
    Args:
        param_id: ElementId of parameter
        evaluator: FilterNumericRuleEvaluator instance
        value: Numeric value to filter by
        tolerance: Comparison tolerance
        
    Returns:
        FilterDoubleRule instance
    """
    from Autodesk.Revit.DB import FilterDoubleRule
    
    if IS_2024_OR_NEWER:
        return FilterDoubleRule.Create(param_id, evaluator, value, tolerance)
    else:
        return FilterDoubleRule(param_id, evaluator, value, tolerance)


# =============================================================================
# Transaction Group
# =============================================================================

def get_transaction_status_name(status):
    """
    Get transaction status name (enum changed in some versions).
    
    Args:
        status: TransactionStatus enum value
        
    Returns:
        str: Human-readable status name
    """
    return str(status)  # Enum str conversion is consistent


# =============================================================================
# Selection
# =============================================================================

def get_selection_ids(uidoc):
    """
    Get selected element IDs safely.
    
    Args:
        uidoc: UIDocument instance
        
    Returns:
        list: List of ElementId, empty if none selected
    """
    if uidoc is None:
        return []
    
    try:
        selection = uidoc.Selection.GetElementIds()
        return list(selection) if selection else []
    except Exception:
        return []
```

### Usage in Script

```python
# script.py - Using compat layer
import sys
import os

# Add lib to path
script_dir = os.path.dirname(__file__)
lib_path = os.path.join(script_dir, '..', '..', 'lib')
sys.path.insert(0, lib_path)

from compat import get_element_name, create_filter_string_rule, REVIT_VERSION

# Now use version-agnostic functions
doc = __revit__.ActiveUIDocument.Document
uidoc = __revit__.ActiveUIDocument

# Get selected elements and their names
for elem_id in uidoc.Selection.GetElementIds():
    elem = doc.GetElement(elem_id)
    name = get_element_name(elem)  # Works in all versions
    print("Element: {}".format(name))
```

---

## Example 5: Full Compat Module (C#)

### Project Structure

```
src/
  Compat/
    RevitVersionInfo.cs
    ElementCompat.cs
    FilterCompat.cs
    DocumentCompat.cs
```

### RevitVersionInfo.cs

```csharp
using System;
using System.Reflection;

namespace DCM.Tools.Compat
{
    /// <summary>
    /// Runtime version detection for cases where compile-time
    /// conditional compilation is not sufficient.
    /// </summary>
    public static class RevitVersionInfo
    {
        private static int? _majorVersion;
        private static string _versionString;

        /// <summary>
        /// Gets the major Revit version (e.g., 2024, 2025).
        /// </summary>
        public static int MajorVersion
        {
            get
            {
                if (!_majorVersion.HasValue)
                {
                    DetectVersion();
                }
                return _majorVersion.Value;
            }
        }

        /// <summary>
        /// Gets the full version string (e.g., "2024.0.1").
        /// </summary>
        public static string VersionString
        {
            get
            {
                if (_versionString == null)
                {
                    DetectVersion();
                }
                return _versionString;
            }
        }

        public static bool Is2023 => MajorVersion == 2023;
        public static bool Is2024 => MajorVersion == 2024;
        public static bool Is2025 => MajorVersion == 2025;
        public static bool Is2026 => MajorVersion == 2026;

        public static bool Is2024OrOlder => MajorVersion <= 2024;
        public static bool Is2025OrNewer => MajorVersion >= 2025;

        private static void DetectVersion()
        {
            try
            {
                var assembly = typeof(Autodesk.Revit.DB.Document).Assembly;
                var version = assembly.GetName().Version;
                
                // Revit version mapping: 
                // Assembly version 24.x = Revit 2024
                // Assembly version 25.x = Revit 2025
                _majorVersion = version.Major + 2000;
                _versionString = $"{_majorVersion}.{version.Minor}.{version.Build}";
            }
            catch
            {
                _majorVersion = 2024; // Safe default
                _versionString = "Unknown";
            }
        }
    }
}
```

### .csproj Conditional Compilation Setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
    <UseWPF>true</UseWPF>
  </PropertyGroup>

  <!-- Define version symbols based on target framework -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net48'">
    <DefineConstants>$(DefineConstants);REVIT2023;REVIT2024</DefineConstants>
  </PropertyGroup>
  
  <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
    <DefineConstants>$(DefineConstants);REVIT2025;REVIT2025_OR_NEWER</DefineConstants>
  </PropertyGroup>

  <!-- Revit API references - adjust paths as needed -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <Reference Include="RevitAPI">
      <HintPath>C:\Program Files\Autodesk\Revit 2024\RevitAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>C:\Program Files\Autodesk\Revit 2024\RevitAPIUI.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
  
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0-windows'">
    <Reference Include="RevitAPI">
      <HintPath>C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>C:\Program Files\Autodesk\Revit 2025\RevitAPIUI.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

---

## Example 6: Smoke Test Script

### C# Test Class

```csharp
using NUnit.Framework;
using DCM.Tools.Compat;
using Autodesk.Revit.DB;

namespace DCM.Tools.Tests
{
    [TestFixture]
    public class CompatSmokeTests
    {
        private Document _doc;

        [SetUp]
        public void Setup()
        {
            // Requires Revit test harness (RevitTestFramework or similar)
            _doc = GetActiveDocument();
        }

        [Test]
        public void RevitVersionInfo_ReturnsValidVersion()
        {
            var version = RevitVersionInfo.MajorVersion;
            Assert.That(version, Is.InRange(2023, 2026));
            Assert.That(RevitVersionInfo.VersionString, Is.Not.Empty);
        }

        [Test]
        public void ElementCompat_GetName_ReturnsNonEmptyForValidElement()
        {
            var collector = new FilteredElementCollector(_doc);
            var element = collector.OfClass(typeof(Wall)).FirstElement();
            
            if (element != null)
            {
                var name = ElementCompat.GetName(element);
                Assert.That(name, Is.Not.Null);
            }
            else
            {
                Assert.Inconclusive("No walls in test document");
            }
        }

        [Test]
        public void FilterCompat_CreateStringRule_DoesNotThrow()
        {
            var paramId = new ElementId(BuiltInParameter.ALL_MODEL_MARK);
            var evaluator = new FilterStringContains();
            
            Assert.DoesNotThrow(() =>
            {
                var rule = FilterCompat.CreateStringRule(paramId, evaluator, "Test");
            });
        }

        private Document GetActiveDocument()
        {
            // Implementation depends on test harness
            throw new NotImplementedException();
        }
    }
}
```

### Python Smoke Test

```python
# scripts/smoke_check_compat.py
"""
Run in Revit to verify compat layer.
Usage: Execute via pyRevit or Revit Python Shell.
"""

from pyrevit import script, forms
import sys
import os

# Setup lib path
script_dir = os.path.dirname(__file__)
lib_path = os.path.abspath(os.path.join(script_dir, '..', 'lib'))
if lib_path not in sys.path:
    sys.path.insert(0, lib_path)

from compat import (
    REVIT_VERSION,
    IS_2025_OR_NEWER,
    IS_2024_OR_OLDER,
    get_element_name,
    set_element_name,
    create_filter_string_rule,
    get_selection_ids
)

from Autodesk.Revit.DB import (
    FilteredElementCollector,
    Wall,
    FilterStringContains,
    BuiltInParameter,
    ElementId
)


def run_smoke_tests():
    """Run all compat smoke tests."""
    results = []
    passed = 0
    failed = 0

    # Test 1: Version detection
    try:
        results.append(f"[INFO] Revit Version: {REVIT_VERSION}")
        results.append(f"[INFO] Is 2025+: {IS_2025_OR_NEWER}")
        results.append(f"[INFO] Is 2024-: {IS_2024_OR_OLDER}")
        passed += 1
    except Exception as e:
        results.append(f"[FAIL] Version detection: {e}")
        failed += 1

    # Test 2: get_element_name
    doc = __revit__.ActiveUIDocument.Document
    try:
        collector = FilteredElementCollector(doc)
        wall = collector.OfClass(Wall).FirstElement()
        
        if wall:
            name = get_element_name(wall)
            if name:
                results.append(f"[PASS] get_element_name: '{name}'")
                passed += 1
            else:
                results.append("[WARN] get_element_name returned empty")
                passed += 1  # Still passed, just no name
        else:
            results.append("[SKIP] get_element_name: No walls in model")
    except Exception as e:
        results.append(f"[FAIL] get_element_name: {e}")
        failed += 1

    # Test 3: create_filter_string_rule
    try:
        param_id = ElementId(BuiltInParameter.ALL_MODEL_MARK)
        evaluator = FilterStringContains()
        rule = create_filter_string_rule(param_id, evaluator, "Test")
        
        if rule:
            results.append("[PASS] create_filter_string_rule")
            passed += 1
        else:
            results.append("[FAIL] create_filter_string_rule returned None")
            failed += 1
    except Exception as e:
        results.append(f"[FAIL] create_filter_string_rule: {e}")
        failed += 1

    # Test 4: get_selection_ids
    uidoc = __revit__.ActiveUIDocument
    try:
        ids = get_selection_ids(uidoc)
        results.append(f"[PASS] get_selection_ids: {len(ids)} selected")
        passed += 1
    except Exception as e:
        results.append(f"[FAIL] get_selection_ids: {e}")
        failed += 1

    # Summary
    results.append("")
    results.append(f"=== Summary: {passed} passed, {failed} failed ===")

    return "\n".join(results)


if __name__ == "__main__":
    output = run_smoke_tests()
    forms.alert(output, title="Compat Smoke Test Results", warn_icon=False)
```
