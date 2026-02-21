using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CheckCode
{
    /// <summary>
    /// Entry point for pyRevit launcher.
    /// </summary>
    public static class Entry
    {
        /// <summary>
        /// Main entry point - called by pyRevit script.
        /// </summary>
        /// <param name="uiapp">Revit UIApplication instance</param>
        public static void Run(UIApplication uiapp)
        {
            try
            {
                // Guard null
                if (uiapp == null)
                {
                    TaskDialog.Show("CheckCode", "Error: UIApplication is null.");
                    return;
                }

                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show("CheckCode", "Please open a document first.");
                    return;
                }

                var doc = uidoc.Document;
                if (doc == null)
                {
                    TaskDialog.Show("CheckCode", "Error: Document is null.");
                    return;
                }

                // Run the check
                var checker = new CodeChecker(doc);
                var result = checker.RunAllChecks();

                // Show results
                ShowResults(result);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("CheckCode Error", 
                    $"An error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}");
            }
        }

        private static void ShowResults(CheckResult result)
        {
            if (result == null)
            {
                TaskDialog.Show("CheckCode", "Check returned no result.");
                return;
            }

            var issues = result.Issues ?? new List<CheckIssue>();
            var sb = new StringBuilder();
            sb.AppendLine("=== CHECK CODE RESULTS ===");
            sb.AppendLine();
            sb.AppendLine($"Total elements checked: {result.TotalElementsChecked}");
            sb.AppendLine($"Issues found: {issues.Count}");
            sb.AppendLine();

            if (issues.Count == 0)
            {
                sb.AppendLine("✓ No issues found!");
            }
            else
            {
                sb.AppendLine("Issues:");
                sb.AppendLine(new string('-', 40));
                
                foreach (var issue in issues.Take(20))
                {
                    sb.AppendLine($"• [{issue.Category}] {issue.Description}");
                    if (issue.ElementId != ElementId.InvalidElementId)
                    {
#if REVIT2025
                        sb.AppendLine($"  Element ID: {issue.ElementId.Value}");
#else
                        sb.AppendLine($"  Element ID: {issue.ElementId.IntegerValue}");
#endif
                    }
                }

                if (issues.Count > 20)
                {
                    sb.AppendLine($"\n... and {issues.Count - 20} more issues.");
                }
            }

            TaskDialog.Show("CheckCode Results", sb.ToString());
        }
    }

    /// <summary>
    /// Result of code checking.
    /// </summary>
    public class CheckResult
    {
        public int TotalElementsChecked { get; set; }
        public List<CheckIssue> Issues { get; set; } = new List<CheckIssue>();
    }

    /// <summary>
    /// Single issue found during checking.
    /// </summary>
    public class CheckIssue
    {
        public string Category { get; set; }
        public string Description { get; set; }
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
        public IssueSeverity Severity { get; set; } = IssueSeverity.Warning;
    }

    /// <summary>
    /// Issue severity levels.
    /// </summary>
    public enum IssueSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Code checker class - performs various checks on Revit model.
    /// </summary>
    public class CodeChecker
    {
        private readonly Document _doc;
        private readonly CheckResult _result;

        public CodeChecker(Document doc)
        {
            _doc = doc;
            _result = new CheckResult();
        }

        /// <summary>
        /// Run all checks and return results.
        /// </summary>
        public CheckResult RunAllChecks()
        {
            CheckWallsWithoutMark();
            CheckFloorsWithoutMark();
            CheckDoorsWithoutMark();
            CheckWindowsWithoutMark();
            CheckDuplicateMarks();

            return _result;
        }

        private void CheckWallsWithoutMark()
        {
            var walls = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .ToList();

            foreach (var wall in walls)
            {
                _result.TotalElementsChecked++;
                
                var mark = wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrWhiteSpace(mark))
                {
                    _result.Issues.Add(new CheckIssue
                    {
                        Category = "Wall",
                        Description = "Wall without Mark parameter",
                        ElementId = wall.Id,
                        Severity = IssueSeverity.Warning
                    });
                }
            }
        }

        private void CheckFloorsWithoutMark()
        {
            var floors = new FilteredElementCollector(_doc)
                .OfClass(typeof(Floor))
                .Cast<Floor>()
                .ToList();

            foreach (var floor in floors)
            {
                _result.TotalElementsChecked++;
                
                var mark = floor.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrWhiteSpace(mark))
                {
                    _result.Issues.Add(new CheckIssue
                    {
                        Category = "Floor",
                        Description = "Floor without Mark parameter",
                        ElementId = floor.Id,
                        Severity = IssueSeverity.Warning
                    });
                }
            }
        }

        private void CheckDoorsWithoutMark()
        {
            var doors = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var door in doors)
            {
                _result.TotalElementsChecked++;
                
                var mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrWhiteSpace(mark))
                {
                    _result.Issues.Add(new CheckIssue
                    {
                        Category = "Door",
                        Description = "Door without Mark parameter",
                        ElementId = door.Id,
                        Severity = IssueSeverity.Warning
                    });
                }
            }
        }

        private void CheckWindowsWithoutMark()
        {
            var windows = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var window in windows)
            {
                _result.TotalElementsChecked++;
                
                var mark = window.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrWhiteSpace(mark))
                {
                    _result.Issues.Add(new CheckIssue
                    {
                        Category = "Window",
                        Description = "Window without Mark parameter",
                        ElementId = window.Id,
                        Severity = IssueSeverity.Warning
                    });
                }
            }
        }

        private void CheckDuplicateMarks()
        {
            var markByCategoryMap = new Dictionary<(long catId, string mark), List<ElementId>>();

            var collector = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType();

            foreach (var elem in collector)
            {
                if (elem.Category == null) continue;
                var mark = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (string.IsNullOrWhiteSpace(mark)) continue;

                var key = (elem.Category.Id.Value, mark);
                if (!markByCategoryMap.TryGetValue(key, out var ids))
                {
                    ids = new List<ElementId>();
                    markByCategoryMap[key] = ids;
                }
                ids.Add(elem.Id);
            }

            foreach (var kvp in markByCategoryMap)
            {
                if (kvp.Value.Count <= 1) continue;
                var mark = kvp.Key.mark;
                foreach (var elemId in kvp.Value.Skip(1))
                {
                    var catName = _doc.GetElement(kvp.Value[0])?.Category?.Name ?? "Unknown";
                    _result.Issues.Add(new CheckIssue
                    {
                        Category = "Duplicate Mark",
                        Description = $"Duplicate mark '{mark}' in {catName}",
                        ElementId = elemId,
                        Severity = IssueSeverity.Error
                    });
                }
            }
        }
    }
}
