#!/usr/bin/env python3
"""
Revit Collector Performance Profiler

Analyzes C# or Python files for FilteredElementCollector usage patterns
and identifies potential performance issues.

Usage:
    python profile_collectors.py <file_path>
    python profile_collectors.py <directory> --recursive
"""

import re
import sys
from pathlib import Path
from dataclasses import dataclass
from typing import List, Tuple

@dataclass
class CollectorIssue:
    file: str
    line_number: int
    issue_type: str
    severity: str  # "high", "medium", "low"
    code_snippet: str
    suggestion: str

# Anti-patterns to detect
PATTERNS = {
    # High severity - major performance issues
    "early_to_elements": {
        "pattern": r"FilteredElementCollector\([^)]+\)\s*\.ToElements\(\)",
        "severity": "high",
        "message": "ToElements() called immediately without filtering",
        "suggestion": "Add OfClass/OfCategory before ToElements()"
    },
    "linq_where_after_collector": {
        "pattern": r"\.ToElements\(\)[^;]*\.Where\(",
        "severity": "high", 
        "message": "LINQ Where() used after ToElements() materialization",
        "suggestion": "Use WherePasses() with ElementParameterFilter instead"
    },
    "linq_oftype_after_collector": {
        "pattern": r"\.ToElements\(\)[^;]*\.OfType<",
        "severity": "high",
        "message": "LINQ OfType<> used after ToElements()",
        "suggestion": "Use OfClass(typeof(T)) before ToElements()"
    },
    
    # Medium severity - could be improved
    "no_class_or_category": {
        "pattern": r"FilteredElementCollector\([^)]+\)\s*\.(WhereElementIsNotElementType|WherePasses)",
        "severity": "medium",
        "message": "Collector without OfClass/OfCategory before other filters",
        "suggestion": "Add OfClass or OfCategory as first filter for better performance"
    },
    "slow_filter_before_quick": {
        "pattern": r"\.WherePasses\([^)]+\)\s*\.Of(Class|Category)",
        "severity": "medium",
        "message": "Slow filter (WherePasses) applied before quick filter",
        "suggestion": "Move OfClass/OfCategory before WherePasses"
    },
    "multiple_count_any": {
        "pattern": r"(\.Count\(\)[^;]*\.Any\(\)|\.Any\(\)[^;]*\.Count\(\))",
        "severity": "medium",
        "message": "Multiple enumeration methods on collector",
        "suggestion": "Cache to list first, then use Count/Any on cached list"
    },
    
    # Low severity - minor improvements
    "storing_elements_in_list": {
        "pattern": r"List<Element>\s+\w+\s*=.*\.ToElements\(\)",
        "severity": "low",
        "message": "Storing Element objects in list (memory pressure)",
        "suggestion": "Consider using ToElementIds() if full Element not needed"
    },
    "collector_in_loop": {
        "pattern": r"(for|foreach|while)[^{]*\{[^}]*FilteredElementCollector",
        "severity": "medium",
        "message": "FilteredElementCollector created inside loop",
        "suggestion": "Move collector outside loop if possible, or cache results"
    },
}

# Python-specific patterns
PYTHON_PATTERNS = {
    "python_list_comp_filter": {
        "pattern": r"\[[^\]]+for[^\]]+in[^\]]+FilteredElementCollector[^\]]+if[^\]]+\]",
        "severity": "high",
        "message": "Python list comprehension with filtering on collector",
        "suggestion": "Use API filters (OfClass, OfCategory, WherePasses) instead"
    },
    "python_isinstance_filter": {
        "pattern": r"isinstance\([^,]+,\s*(DB\.)?[A-Z]\w+\)",
        "severity": "medium",
        "message": "Python isinstance check (could use OfClass instead)",
        "suggestion": "Use FilteredElementCollector.OfClass() for type filtering"
    },
}


def detect_file_type(file_path: Path) -> str:
    """Detect if file is C# or Python."""
    suffix = file_path.suffix.lower()
    if suffix == ".cs":
        return "csharp"
    elif suffix == ".py":
        return "python"
    return "unknown"


def analyze_file(file_path: Path) -> List[CollectorIssue]:
    """Analyze a single file for collector issues."""
    issues = []
    
    file_type = detect_file_type(file_path)
    if file_type == "unknown":
        return issues
    
    try:
        content = file_path.read_text(encoding='utf-8')
    except Exception as e:
        print(f"Error reading {file_path}: {e}", file=sys.stderr)
        return issues
    
    lines = content.split('\n')
    
    # Select patterns based on file type
    patterns_to_check = PATTERNS.copy()
    if file_type == "python":
        patterns_to_check.update(PYTHON_PATTERNS)
    
    # Check each pattern
    for pattern_name, pattern_info in patterns_to_check.items():
        regex = re.compile(pattern_info["pattern"], re.MULTILINE | re.DOTALL)
        
        for match in regex.finditer(content):
            # Find line number
            line_start = content.count('\n', 0, match.start()) + 1
            
            # Get code snippet (the matched line and context)
            start_line = max(0, line_start - 2)
            end_line = min(len(lines), line_start + 2)
            snippet = '\n'.join(f"{i+1}: {lines[i]}" for i in range(start_line, end_line))
            
            issues.append(CollectorIssue(
                file=str(file_path),
                line_number=line_start,
                issue_type=pattern_name,
                severity=pattern_info["severity"],
                code_snippet=snippet,
                suggestion=pattern_info["suggestion"]
            ))
    
    return issues


def analyze_path(path: Path, recursive: bool = False) -> List[CollectorIssue]:
    """Analyze a file or directory."""
    issues = []
    
    if path.is_file():
        issues.extend(analyze_file(path))
    elif path.is_dir():
        pattern = "**/*" if recursive else "*"
        for ext in [".cs", ".py"]:
            for file_path in path.glob(f"{pattern}{ext}"):
                issues.extend(analyze_file(file_path))
    
    return issues


def format_report(issues: List[CollectorIssue]) -> str:
    """Format issues into a readable report."""
    if not issues:
        return "✅ No collector performance issues found!"
    
    # Group by severity
    high = [i for i in issues if i.severity == "high"]
    medium = [i for i in issues if i.severity == "medium"]
    low = [i for i in issues if i.severity == "low"]
    
    report = []
    report.append("=" * 60)
    report.append("REVIT COLLECTOR PERFORMANCE REPORT")
    report.append("=" * 60)
    report.append(f"\nTotal issues found: {len(issues)}")
    report.append(f"  🔴 High:   {len(high)}")
    report.append(f"  🟡 Medium: {len(medium)}")
    report.append(f"  🟢 Low:    {len(low)}")
    report.append("")
    
    def format_issue_group(title: str, issues: List[CollectorIssue], emoji: str):
        if not issues:
            return []
        lines = [f"\n{emoji} {title.upper()} SEVERITY ISSUES", "-" * 40]
        for issue in issues:
            lines.append(f"\n📍 {issue.file}:{issue.line_number}")
            lines.append(f"   Issue: {issue.issue_type}")
            lines.append(f"   {issue.suggestion}")
            lines.append(f"\n   Code:\n{issue.code_snippet}\n")
        return lines
    
    report.extend(format_issue_group("High", high, "🔴"))
    report.extend(format_issue_group("Medium", medium, "🟡"))
    report.extend(format_issue_group("Low", low, "🟢"))
    
    return '\n'.join(report)


def main():
    if len(sys.argv) < 2:
        print("Usage: python profile_collectors.py <file_or_directory> [--recursive]")
        sys.exit(1)
    
    path = Path(sys.argv[1])
    recursive = "--recursive" in sys.argv or "-r" in sys.argv
    
    if not path.exists():
        print(f"Error: Path '{path}' does not exist", file=sys.stderr)
        sys.exit(1)
    
    issues = analyze_path(path, recursive)
    report = format_report(issues)
    print(report)
    
    # Exit with non-zero if high severity issues found
    high_count = sum(1 for i in issues if i.severity == "high")
    sys.exit(1 if high_count > 0 else 0)


if __name__ == "__main__":
    main()
