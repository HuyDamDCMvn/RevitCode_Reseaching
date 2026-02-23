using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitChat.Models;
using RevitChat.Skills;

namespace RevitChat.Handler
{
    /// <summary>
    /// ExternalEvent handler that executes tool calls on the Revit main thread.
    /// Routes each tool call to the appropriate skill via SkillRegistry.
    /// </summary>
    public class RevitChatHandler : IExternalEventHandler
    {
        private const int MaxTransactionStackSize = 50;

        private readonly ChatRequestQueue _queue;
        private readonly SkillRegistry _skillRegistry;
        private readonly List<string> _lastTransactionNames = new();

        /// <summary>
        /// Fired after all queued tool calls have been executed.
        /// Key = ToolCallId, Value = JSON result string.
        /// </summary>
        public event Action<Dictionary<string, string>> OnToolCallsCompleted;

        public event Action<string> OnError;

        #pragma warning disable CS0067 // reserved for future skill progress reporting
        public event Action<string> OnProgress;
        #pragma warning restore CS0067

        public IReadOnlyCollection<string> LastTransactionNames => _lastTransactionNames;

        public string LastTransactionName => _lastTransactionNames.Count > 0 ? _lastTransactionNames[0] : null;

        public bool TryPopLastTransaction(out string name)
        {
            if (_lastTransactionNames.Count > 0)
            {
                name = _lastTransactionNames[0];
                _lastTransactionNames.RemoveAt(0);
                return true;
            }
            name = null;
            return false;
        }

        public RevitChatHandler(ChatRequestQueue queue, SkillRegistry skillRegistry)
        {
            _queue = queue;
            _skillRegistry = skillRegistry;
        }

        public void Execute(UIApplication app)
        {
            var results = new Dictionary<string, string>();
            var doc = app?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                while (_queue.TryDequeue(out var request))
                    results[request.ToolCallId] = JsonSerializer.Serialize(
                        new { error = "No active document. Open a Revit document and try again." });
                OnToolCallsCompleted?.Invoke(results);
                return;
            }

            try
            {
                var method = doc.GetType().GetMethod("IsInEditMode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (method != null && method.Invoke(doc, null) is bool inEdit && inEdit)
                {
                    while (_queue.TryDequeue(out var request))
                        results[request.ToolCallId] = JsonSerializer.Serialize(
                            new { error = "Revit is in edit mode. Finish the current operation and try again." });
                    OnToolCallsCompleted?.Invoke(results);
                    return;
                }
            }
            catch { }

            try
            {
                while (_queue.TryDequeue(out var request))
                {
                    try
                    {
                        var result = _skillRegistry.ExecuteTool(
                            request.FunctionName, app, request.Arguments);
                        results[request.ToolCallId] = result;
                        if (!string.IsNullOrEmpty(result) && !result.Contains("\"error\""))
                        {
                            _lastTransactionNames.Insert(0, request.FunctionName);
                            if (_lastTransactionNames.Count > MaxTransactionStackSize)
                                _lastTransactionNames.RemoveAt(_lastTransactionNames.Count - 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        results[request.ToolCallId] =
                            JsonSerializer.Serialize(new { error = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"RevitChatHandler error: {ex.Message}");
            }
            finally
            {
                OnToolCallsCompleted?.Invoke(results);
            }
        }

        public string GetName() => "RevitChat.Handler";
    }
}
