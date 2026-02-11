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
            var sb = new StringBuilder();
            sb.AppendLine("=== CHECK CODE RESULTS ===");
            sb.AppendLine();
            sb.AppendLine($"Total elements checked: {result.TotalElementsChecked}");
            sb.AppendLine($"Issues found: {result.Issues.Count}");
            sb.AppendLine();

            if (result.Issues.Count == 0)
            {
                sb.AppendLine("✓ No issues found!");
            }
            else
            {
                sb.AppendLine("Issues:");
                sb.AppendLine(new string('-', 40));
                
                foreach (var issue in result.Issues.Take(20)) // Limit to 20 issues
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

                if (result.Issues.Count > 20)
                {
                    sb.AppendLine($"\n... and {result.Issues.Count - 20} more issues.");
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
            // Check for duplicate marks across all elements
            var allElements = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null)
                .ToList();

            var markGroups = allElements
                .Select(e => new
                {
                    Element = e,
                    Mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Mark))
                .GroupBy(x => x.Mark)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in markGroups)
            {
                var elements = group.ToList();
                var categories = elements
                    .Select(x => x.Element.Category?.Name ?? "Unknown")
                    .Distinct()
                    .ToList();

                // Only report if same mark used in same category
                var sameCategoryDuplicates = elements
                    .GroupBy(x => x.Element.Category?.Id ?? ElementId.InvalidElementId)
                    .Where(g => g.Count() > 1)
                    .ToList();

                foreach (var catGroup in sameCategoryDuplicates)
                {
                    foreach (var item in catGroup.Skip(1)) // Skip first, report rest as duplicates
                    {
                        _result.Issues.Add(new CheckIssue
                        {
                            Category = "Duplicate Mark",
                            Description = $"Duplicate mark '{group.Key}' in {item.Element.Category?.Name}",
                            ElementId = item.Element.Id,
                            Severity = IssueSeverity.Error
                        });
                    }
                }
            }
        }
    }
}
