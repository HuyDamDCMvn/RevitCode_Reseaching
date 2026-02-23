using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenAI.Chat;

namespace RevitChat.Skills
{
    public abstract class BaseRevitSkill : IRevitSkill
    {
        protected abstract string SkillName { get; }
        protected abstract string SkillDescription { get; }
        protected abstract HashSet<string> HandledFunctions { get; }

        public string Name => SkillName;
        public string Description => SkillDescription;
        public bool CanHandle(string functionName) => HandledFunctions.Contains(functionName);

        public abstract IReadOnlyList<ChatTool> GetToolDefinitions();

        public string Execute(string functionName, UIApplication app, Dictionary<string, object> args)
        {
            var uidoc = app?.ActiveUIDocument;
            if (uidoc == null)
                return RevitHelpers.JsonError("No active document.");
            return ExecuteTool(functionName, uidoc, uidoc.Document, args);
        }

        protected abstract string ExecuteTool(string functionName, UIDocument uidoc, Document doc, Dictionary<string, object> args);

        protected string UnknownTool(string tool)
            => RevitHelpers.JsonError($"{SkillName}: unknown tool '{tool}'");

        protected static string GetString(Dictionary<string, object> args, string key, string defaultValue = null)
        {
            if (args == null || !args.TryGetValue(key, out var val)) return defaultValue;
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String) return je.GetString();
                if (je.ValueKind == JsonValueKind.Null) return defaultValue;
                return je.ToString();
            }
            return val?.ToString() ?? defaultValue;
        }

        protected static int GetInt(Dictionary<string, object> args, string key, int defaultValue = 0)
        {
            if (args == null || !args.TryGetValue(key, out var val)) return defaultValue;
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number) return je.GetInt32();
                if (int.TryParse(je.ToString(), out var parsed)) return parsed;
                return defaultValue;
            }
            if (val is int i) return i;
            if (int.TryParse(val?.ToString(), out var p)) return p;
            return defaultValue;
        }

        protected static double GetDouble(Dictionary<string, object> args, string key, double defaultValue = 0)
        {
            if (args == null || !args.TryGetValue(key, out var val)) return defaultValue;
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number) return je.GetDouble();
                if (double.TryParse(je.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
                return defaultValue;
            }
            if (val is double d) return d;
            if (double.TryParse(val?.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
            return defaultValue;
        }

        protected static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue = false)
        {
            if (args == null || !args.TryGetValue(key, out var val)) return defaultValue;
            if (val is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True) return true;
                if (je.ValueKind == JsonValueKind.False) return false;
                return defaultValue;
            }
            if (val is bool b) return b;
            return defaultValue;
        }

        protected static List<int> GetElementIds(Dictionary<string, object> args, string key = "element_ids")
        {
            if (args == null || !args.TryGetValue(key, out var val)) return new List<int>();
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<int>();
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number)
                        ids.Add(item.GetInt32());
                    else if (int.TryParse(item.ToString(), out var parsed))
                        ids.Add(parsed);
                }
                return ids;
            }
            return new List<int>();
        }

        protected static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out var val)) return new List<string>();
            if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList();
            return new List<string>();
        }

        protected static string RunInTransaction(Document doc, string transactionName, Action action, bool suppressWarnings = false)
        {
            using (var t = new Transaction(doc, transactionName))
            {
                if (suppressWarnings)
                    t.SetFailureHandlingOptions(t.GetFailureHandlingOptions()
                        .SetFailuresPreprocessor(new SilentFailureProcessor()));
                t.Start();
                try
                {
                    action();
                    t.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    if (t.HasStarted()) t.RollBack();
                    return ex.Message;
                }
            }
        }

        protected static string RunInTransaction(Document doc, string transactionName, Func<string> action, bool suppressWarnings = false)
        {
            using (var t = new Transaction(doc, transactionName))
            {
                if (suppressWarnings)
                    t.SetFailureHandlingOptions(t.GetFailureHandlingOptions()
                        .SetFailuresPreprocessor(new SilentFailureProcessor()));
                t.Start();
                try
                {
                    var result = action();
                    t.Commit();
                    return result;
                }
                catch (Exception ex)
                {
                    if (t.HasStarted()) t.RollBack();
                    return $"Error: {ex.Message}";
                }
            }
        }

        internal sealed class SilentFailureProcessor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var msg in fa.GetFailureMessages())
                {
                    var severity = msg.GetSeverity();
                    if (severity == FailureSeverity.Warning)
                    {
                        fa.DeleteWarning(msg);
                    }
                    else
                    {
                        try { fa.ResolveFailure(msg); }
                        catch
                        {
                            if (severity == FailureSeverity.Warning)
                                fa.DeleteWarning(msg);
                        }
                    }
                }
                return FailureProcessingResult.Continue;
            }
        }
    }
}
