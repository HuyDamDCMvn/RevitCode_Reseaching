using System;
using System.Collections.Generic;
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
        private readonly ChatRequestQueue _queue;
        private readonly SkillRegistry _skillRegistry;

        /// <summary>
        /// Fired after all queued tool calls have been executed.
        /// Key = ToolCallId, Value = JSON result string.
        /// </summary>
        public event Action<Dictionary<string, string>> OnToolCallsCompleted;

        public event Action<string> OnError;

        public RevitChatHandler(ChatRequestQueue queue, SkillRegistry skillRegistry)
        {
            _queue = queue;
            _skillRegistry = skillRegistry;
        }

        public void Execute(UIApplication app)
        {
            var results = new Dictionary<string, string>();

            try
            {
                while (_queue.TryDequeue(out var request))
                {
                    try
                    {
                        var result = _skillRegistry.ExecuteTool(
                            request.FunctionName, app, request.Arguments);
                        results[request.ToolCallId] = result;
                    }
                    catch (Exception ex)
                    {
                        results[request.ToolCallId] =
                            JsonSerializer.Serialize(new { error = ex.Message });
                    }
                }

                OnToolCallsCompleted?.Invoke(results);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"RevitChatHandler error: {ex.Message}");
            }
        }

        public string GetName() => "RevitChat.Handler";
    }
}
