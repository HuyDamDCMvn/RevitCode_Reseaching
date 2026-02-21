using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;
using static RevitChat.Skills.RevitHelpers;

namespace RevitChat.Skills
{
    public class FilterTemplateSkill : IRevitSkill
    {
        public string Name => "FilterTemplate";
        public string Description => "Manage view filters, view templates, and parameter filter rules";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_view_filters", "get_view_templates", "apply_view_template",
            "create_parameter_filter", "get_filter_rules"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_view_filters",
                "List all ParameterFilterElements in the document, with their rules and applied views.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter_name": { "type": "string", "description": "Optional: filter by name (partial match)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("get_view_templates",
                "List all view templates in the document.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "view_type": { "type": "string", "description": "Optional: filter by ViewType (FloorPlan, Section, ThreeD, etc.)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("apply_view_template",
                "Apply a view template to one or more views. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "view_ids": { "type": "array", "items": { "type": "integer" }, "description": "View IDs to apply template to" },
                        "template_id": { "type": "integer", "description": "View template ID" }
                    },
                    "required": ["view_ids", "template_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("create_parameter_filter",
                "Create a new ParameterFilterElement. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter_name": { "type": "string", "description": "Name for the new filter" },
                        "categories": { "type": "array", "items": { "type": "string" }, "description": "Category names to apply filter to" },
                        "parameter_name": { "type": "string", "description": "Parameter to filter on" },
                        "rule_type": { "type": "string", "enum": ["equals", "not_equals", "contains", "does_not_contain", "begins_with", "ends_with", "greater", "less"], "description": "Rule comparison type" },
                        "value": { "type": "string", "description": "Value to compare against" }
                    },
                    "required": ["filter_name", "categories", "parameter_name", "rule_type", "value"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_filter_rules",
                "Get the rules defined in a specific ParameterFilterElement.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "filter_id": { "type": "integer", "description": "ParameterFilterElement ID" }
                    },
                    "required": ["filter_id"]
                }
                """))
        };

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return JsonError("No active document.");
            var doc = uidoc.Document;

            return functionName switch
            {
                "get_view_filters" => GetViewFilters(doc, args),
                "get_view_templates" => GetViewTemplates(doc, args),
                "apply_view_template" => ApplyViewTemplate(doc, args),
                "create_parameter_filter" => CreateParameterFilter(doc, args),
                "get_filter_rules" => GetFilterRules(doc, args),
                _ => JsonError($"FilterTemplateSkill: unknown tool '{functionName}'")
            };
        }

        private string GetViewFilters(Document doc, Dictionary<string, object> args)
        {
            var nameFilter = GetArg<string>(args, "filter_name");
            int limit = GetArg(args, "limit", 50);

            var filters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToList();

            if (!string.IsNullOrEmpty(nameFilter))
                filters = filters.Where(f => f.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var items = filters.Take(limit).Select(f =>
            {
                var catIds = f.GetCategories();
                var catNames = catIds.Select(cid =>
                {
                    var cat = Category.GetCategory(doc, cid);
                    return cat?.Name ?? cid.Value.ToString();
                }).ToList();

                return new
                {
                    id = f.Id.Value,
                    name = f.Name,
                    categories = catNames
                };
            }).ToList();

            return JsonSerializer.Serialize(new { total = filters.Count, returned = items.Count, filters = items }, JsonOpts);
        }

        private string GetViewTemplates(Document doc, Dictionary<string, object> args)
        {
            var typeFilter = GetArg<string>(args, "view_type");
            int limit = GetArg(args, "limit", 50);

            var templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .ToList();

            if (!string.IsNullOrEmpty(typeFilter) && Enum.TryParse<ViewType>(typeFilter, true, out var vt))
                templates = templates.Where(t => t.ViewType == vt).ToList();

            var items = templates.Take(limit).Select(t => new
            {
                id = t.Id.Value,
                name = t.Name,
                view_type = t.ViewType.ToString()
            }).ToList();

            return JsonSerializer.Serialize(new { total = templates.Count, returned = items.Count, templates = items }, JsonOpts);
        }

        private string ApplyViewTemplate(Document doc, Dictionary<string, object> args)
        {
            var viewIds = GetArgLongArray(args, "view_ids");
            long templateId = GetArg<long>(args, "template_id");

            if (viewIds == null || viewIds.Count == 0) return JsonError("view_ids required.");

            var template = doc.GetElement(new ElementId(templateId)) as View;
            if (template == null || !template.IsTemplate)
                return JsonError($"View template {templateId} not found or is not a template.");

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Apply View Template"))
            {
                trans.Start();
                foreach (var vid in viewIds)
                {
                    var view = doc.GetElement(new ElementId(vid)) as View;
                    if (view == null) { errors.Add($"View {vid} not found."); continue; }
                    if (view.IsTemplate) { errors.Add($"View {vid} is a template itself."); continue; }

                    try { view.ViewTemplateId = template.Id; success++; }
                    catch (Exception ex) { errors.Add($"View {vid}: {ex.Message}"); }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new
            {
                success,
                template_name = template.Name,
                errors = errors.Take(10)
            }, JsonOpts);
        }

        private string CreateParameterFilter(Document doc, Dictionary<string, object> args)
        {
            var filterName = GetArg<string>(args, "filter_name");
            var categoryNames = GetArgStringArray(args, "categories");
            var paramName = GetArg<string>(args, "parameter_name");
            var ruleType = GetArg<string>(args, "rule_type");
            var value = GetArg<string>(args, "value");

            if (string.IsNullOrEmpty(filterName) || categoryNames == null || categoryNames.Count == 0)
                return JsonError("filter_name and categories are required.");

            var catIds = new List<ElementId>();
            foreach (var catName in categoryNames)
            {
                var bic = ResolveCategoryFilter(doc, catName);
                if (bic.HasValue)
                    catIds.Add(new ElementId(bic.Value));
            }

            if (catIds.Count == 0)
                return JsonError("No valid categories resolved.");

            Element sampleElement = null;
            foreach (var catId in catIds)
            {
                sampleElement = new FilteredElementCollector(doc)
                    .OfCategoryId(catId)
                    .WhereElementIsNotElementType()
                    .FirstElement();
                if (sampleElement != null) break;
            }

            if (sampleElement == null)
                return JsonError("Cannot find sample element to locate the parameter.");

            var param = sampleElement.LookupParameter(paramName);
            if (param == null)
                return JsonError($"Parameter '{paramName}' not found on elements of the specified categories.");

            var paramId = param.Id;

            using (var trans = new Transaction(doc, "AI: Create Parameter Filter"))
            {
                trans.Start();

                FilterRule rule = null;
                if (param.StorageType == StorageType.String)
                {
                    rule = ruleType switch
                    {
                        "equals" => ParameterFilterRuleFactory.CreateEqualsRule(paramId, value),
                        "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, value),
                        "contains" => ParameterFilterRuleFactory.CreateContainsRule(paramId, value),
                        "does_not_contain" => ParameterFilterRuleFactory.CreateNotContainsRule(paramId, value),
                        "begins_with" => ParameterFilterRuleFactory.CreateBeginsWithRule(paramId, value),
                        "ends_with" => ParameterFilterRuleFactory.CreateEndsWithRule(paramId, value),
                        _ => ParameterFilterRuleFactory.CreateEqualsRule(paramId, value)
                    };
                }
                else if (param.StorageType == StorageType.Double && double.TryParse(value, out double dVal))
                {
                    const double epsilon = 1e-6;
                    rule = ruleType switch
                    {
                        "greater" => ParameterFilterRuleFactory.CreateGreaterRule(paramId, dVal, epsilon),
                        "less" => ParameterFilterRuleFactory.CreateLessRule(paramId, dVal, epsilon),
                        "equals" => ParameterFilterRuleFactory.CreateEqualsRule(paramId, dVal, epsilon),
                        "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, dVal, epsilon),
                        _ => ParameterFilterRuleFactory.CreateEqualsRule(paramId, dVal, epsilon)
                    };
                }
                else if (param.StorageType == StorageType.Integer && int.TryParse(value, out int iVal))
                {
                    rule = ruleType switch
                    {
                        "greater" => ParameterFilterRuleFactory.CreateGreaterRule(paramId, iVal),
                        "less" => ParameterFilterRuleFactory.CreateLessRule(paramId, iVal),
                        "equals" => ParameterFilterRuleFactory.CreateEqualsRule(paramId, iVal),
                        "not_equals" => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, iVal),
                        _ => ParameterFilterRuleFactory.CreateEqualsRule(paramId, iVal)
                    };
                }

                if (rule == null)
                {
                    trans.RollBack();
                    return JsonError("Could not create filter rule for the given parameter type and value.");
                }

                var logicalFilter = new ElementParameterFilter(rule);
                var pfe = ParameterFilterElement.Create(doc, filterName, catIds, logicalFilter);

                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    created = true,
                    id = pfe.Id.Value,
                    name = pfe.Name,
                    categories = categoryNames,
                    parameter = paramName,
                    rule = ruleType,
                    value
                }, JsonOpts);
            }
        }

        private string GetFilterRules(Document doc, Dictionary<string, object> args)
        {
            long filterId = GetArg<long>(args, "filter_id");
            var pfe = doc.GetElement(new ElementId(filterId)) as ParameterFilterElement;
            if (pfe == null) return JsonError($"ParameterFilterElement {filterId} not found.");

            var catIds = pfe.GetCategories();
            var catNames = catIds.Select(cid => Category.GetCategory(doc, cid)?.Name ?? cid.Value.ToString()).ToList();

            var efr = pfe.GetElementFilter();
            string filterDesc = efr?.ToString() ?? "No element filter";

            return JsonSerializer.Serialize(new
            {
                id = pfe.Id.Value,
                name = pfe.Name,
                categories = catNames,
                filter_description = filterDesc
            }, JsonOpts);
        }
    }
}
