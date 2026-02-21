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
    public class GroupSkill : IRevitSkill
    {
        public string Name => "Group";
        public string Description => "Create, list, ungroup, and place groups and their members";

        private static readonly HashSet<string> HandledTools = new()
        {
            "get_groups", "create_group", "ungroup", "get_group_members", "place_group_instance"
        };

        public bool CanHandle(string functionName) => HandledTools.Contains(functionName);

        public IReadOnlyList<ChatTool> GetToolDefinitions() => new List<ChatTool>
        {
            ChatTool.CreateFunctionTool("get_groups",
                "List all model and detail groups in the document.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "group_type": { "type": "string", "enum": ["model", "detail", "all"], "description": "Filter by group type (default: all)" },
                        "limit": { "type": "integer", "description": "Max results (default 50)" }
                    },
                    "required": []
                }
                """)),

            ChatTool.CreateFunctionTool("create_group",
                "Create a group from specified elements. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "element_ids": { "type": "array", "items": { "type": "integer" }, "description": "Element IDs to group" },
                        "group_name": { "type": "string", "description": "Optional name for the new group type" }
                    },
                    "required": ["element_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("ungroup",
                "Ungroup (explode) one or more group instances. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "group_ids": { "type": "array", "items": { "type": "integer" }, "description": "Group instance IDs to ungroup" }
                    },
                    "required": ["group_ids"]
                }
                """)),

            ChatTool.CreateFunctionTool("get_group_members",
                "List the member elements of a group instance.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "group_id": { "type": "integer", "description": "Group instance ID" }
                    },
                    "required": ["group_id"]
                }
                """)),

            ChatTool.CreateFunctionTool("place_group_instance",
                "Place an instance of a group type at a location. Confirm with user first.",
                BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "group_type_id": { "type": "integer", "description": "GroupType ID" },
                        "x": { "type": "number", "description": "X coordinate in feet" },
                        "y": { "type": "number", "description": "Y coordinate in feet" },
                        "z": { "type": "number", "description": "Z coordinate in feet (default 0)" }
                    },
                    "required": ["group_type_id", "x", "y"]
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
                "get_groups" => GetGroups(doc, args),
                "create_group" => CreateGroup(doc, args),
                "ungroup" => Ungroup(doc, args),
                "get_group_members" => GetGroupMembers(doc, args),
                "place_group_instance" => PlaceGroupInstance(doc, args),
                _ => JsonError($"GroupSkill: unknown tool '{functionName}'")
            };
        }

        private string GetGroups(Document doc, Dictionary<string, object> args)
        {
            var typeFilter = GetArg(args, "group_type", "all");
            int limit = GetArg(args, "limit", 50);

            var groups = new FilteredElementCollector(doc)
                .OfClass(typeof(Group))
                .Cast<Group>()
                .ToList();

            if (typeFilter == "model")
                groups = groups.Where(g => g.GroupType?.Category?.Id == new ElementId(BuiltInCategory.OST_IOSModelGroups)).ToList();
            else if (typeFilter == "detail")
                groups = groups.Where(g => g.GroupType?.Category?.Id == new ElementId(BuiltInCategory.OST_IOSDetailGroups)).ToList();

            var grouped = groups
                .GroupBy(g => g.GroupType?.Id.Value ?? 0)
                .Take(limit)
                .Select(grp =>
                {
                    var first = grp.First();
                    var gt = first.GroupType;
                    return new
                    {
                        group_type_id = gt?.Id.Value ?? 0,
                        type_name = gt?.Name ?? "-",
                        category = gt?.Category?.Name ?? "-",
                        instance_count = grp.Count(),
                        sample_instance_id = first.Id.Value,
                        member_count = first.GetMemberIds().Count
                    };
                }).ToList();

            return JsonSerializer.Serialize(new
            {
                total_types = grouped.Count,
                total_instances = groups.Count,
                groups = grouped
            }, JsonOpts);
        }

        private string CreateGroup(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "element_ids");
            var groupName = GetArg<string>(args, "group_name");

            if (ids == null || ids.Count == 0) return JsonError("element_ids required.");

            var elemIds = ids.Select(id => new ElementId(id)).ToList();

            using (var trans = new Transaction(doc, "AI: Create Group"))
            {
                trans.Start();
                var group = doc.Create.NewGroup(elemIds);

                if (!string.IsNullOrEmpty(groupName) && group.GroupType != null)
                    group.GroupType.Name = groupName;

                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    created = true,
                    group_id = group.Id.Value,
                    group_type_id = group.GroupType?.Id.Value ?? 0,
                    name = group.GroupType?.Name ?? "-",
                    member_count = group.GetMemberIds().Count
                }, JsonOpts);
            }
        }

        private string Ungroup(Document doc, Dictionary<string, object> args)
        {
            var ids = GetArgLongArray(args, "group_ids");
            if (ids == null || ids.Count == 0) return JsonError("group_ids required.");

            int success = 0;
            var errors = new List<string>();

            using (var trans = new Transaction(doc, "AI: Ungroup"))
            {
                trans.Start();
                foreach (var id in ids)
                {
                    var group = doc.GetElement(new ElementId(id)) as Group;
                    if (group == null) { errors.Add($"Element {id} is not a Group."); continue; }

                    try { group.UngroupMembers(); success++; }
                    catch (Exception ex) { errors.Add($"Group {id}: {ex.Message}"); }
                }

                if (success > 0) trans.Commit();
                else trans.RollBack();
            }

            return JsonSerializer.Serialize(new { success, errors = errors.Take(10) }, JsonOpts);
        }

        private string GetGroupMembers(Document doc, Dictionary<string, object> args)
        {
            long groupId = GetArg<long>(args, "group_id");
            var group = doc.GetElement(new ElementId(groupId)) as Group;
            if (group == null) return JsonError($"Group {groupId} not found.");

            var memberIds = group.GetMemberIds();
            var members = memberIds.Select(mid =>
            {
                var elem = doc.GetElement(mid);
                return new
                {
                    id = mid.Value,
                    name = elem?.Name ?? "-",
                    category = elem?.Category?.Name ?? "-"
                };
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                group_id = groupId,
                group_name = group.GroupType?.Name ?? "-",
                member_count = members.Count,
                members
            }, JsonOpts);
        }

        private string PlaceGroupInstance(Document doc, Dictionary<string, object> args)
        {
            long typeId = GetArg<long>(args, "group_type_id");
            double x = GetArg(args, "x", 0.0);
            double y = GetArg(args, "y", 0.0);
            double z = GetArg(args, "z", 0.0);

            var groupType = doc.GetElement(new ElementId(typeId)) as GroupType;
            if (groupType == null) return JsonError($"GroupType {typeId} not found.");

            using (var trans = new Transaction(doc, "AI: Place Group Instance"))
            {
                trans.Start();
                var point = new XYZ(x, y, z);
                var instance = doc.Create.PlaceGroup(point, groupType);
                trans.Commit();

                return JsonSerializer.Serialize(new
                {
                    placed = true,
                    group_id = instance.Id.Value,
                    group_type = groupType.Name,
                    location = new { x, y, z }
                }, JsonOpts);
            }
        }
    }
}
