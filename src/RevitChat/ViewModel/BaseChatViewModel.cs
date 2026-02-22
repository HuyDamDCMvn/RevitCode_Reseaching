using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitChat.Handler;
using RevitChat.Models;
using RevitChat.Services;
using RevitChat.Skills;

namespace RevitChat.ViewModel
{
    public abstract partial class BaseChatViewModel : ObservableObject
    {
        private readonly ExternalEvent _externalEvent;
        private readonly RevitChatHandler _handler;
        private readonly ChatRequestQueue _queue;
        private readonly SkillRegistry _skillRegistry;
        private readonly Dispatcher _dispatcher;

        private CancellationTokenSource _cts;
        private TaskCompletionSource<Dictionary<string, string>> _toolResultsTcs;
        private List<ToolCallRequest> _pendingToolCalls;
        private IChatService _diagnosticService;
        private bool _toolExecutedInSession;
        private readonly WorkingMemory _workingMemory = new();
        private string _lastUserPrompt;
        private List<string> _lastToolNames = new();

        private const int AnalyzeThresholdChars = 1200;

        protected abstract IChatService ChatService { get; }
        protected abstract int ToolTimeoutMs { get; }
        protected abstract TimeSpan SendTimeout { get; }
        protected abstract string NotInitializedMessage { get; }
        protected abstract string WelcomeText { get; }

        public ObservableCollection<ChatMessage> Messages { get; } = new();

        [ObservableProperty]
        private string _inputText = "";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        private bool _isSettingsVisible;

        public SkillRegistry SkillRegistry => _skillRegistry;

        protected BaseChatViewModel(ExternalEvent externalEvent, RevitChatHandler handler,
            ChatRequestQueue queue, SkillRegistry skillRegistry)
        {
            _externalEvent = externalEvent;
            _handler = handler;
            _queue = queue;
            _skillRegistry = skillRegistry;
            _dispatcher = Dispatcher.CurrentDispatcher;

            _handler.OnToolCallsCompleted += HandleToolCallsCompleted;
            _handler.OnError += HandleHandlerError;
        }

        protected void AttachChatServiceDiagnostics(IChatService service)
        {
            if (_diagnosticService != null)
                _diagnosticService.DebugMessage -= HandleChatDebug;

            _diagnosticService = service;

            if (_diagnosticService != null)
                _diagnosticService.DebugMessage += HandleChatDebug;
        }

        protected void AddWelcomeMessage()
        {
            var msg = WelcomeText;
            try
            {
                var approved = ChatFeedbackService.ApprovedCount;
                var corrections = ChatFeedbackService.CorrectionCount;
                if (approved + corrections > 0)
                    msg += $"\n({approved} learned patterns, {corrections} corrections)";
            }
            catch { }
            Messages.Add(ChatMessage.FromAssistant(msg));
        }

        protected virtual int MaxToolResultChars => 8000;

        [RelayCommand(CanExecute = nameof(CanSend))]
        private async Task SendAsync()
        {
            var text = InputText?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (!ChatService.IsInitialized)
            {
                StatusMessage = NotInitializedMessage;
                IsSettingsVisible = true;
                return;
            }

            if (_pendingToolCalls != null)
            {
                if (IsConfirmMessage(text))
                {
                    Messages.Add(ChatMessage.FromUser(text));
                    InputText = "";
                    IsBusy = true;
                    StatusMessage = "Executing confirmed action...";

                    _cts = new CancellationTokenSource(SendTimeout);

                    try
                    {
                        var confirmedCalls = _pendingToolCalls;
                        _pendingToolCalls = null;

                        foreach (var tc in confirmedCalls)
                            Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));

                        StatusMessage = $"Executing {confirmedCalls.Count} tool(s)...";
                        var toolResults = await ExecuteToolCallsAsync(confirmedCalls);
                        RemoveToolProgressMessages();

                        var truncated = TruncateToolResults(toolResults);
                        var totalChars = GetTotalChars(truncated);
                        StatusMessage = totalChars > AnalyzeThresholdChars ? "Analyzing results..." : "Finalizing...";

                        var (response, toolCalls) = await ChatService.ContinueWithToolResultsAsync(truncated, _cts.Token);

                        while (toolCalls != null && toolCalls.Count > 0)
                        {
                            if (RequiresConfirmation(toolCalls, out var prompt))
                            {
                                _pendingToolCalls = toolCalls;
                                Messages.Add(ChatMessage.FromAssistant(prompt));
                                StatusMessage = "Awaiting confirmation";
                                return;
                            }

                            foreach (var tc in toolCalls)
                                Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));

                            StatusMessage = $"Executing {toolCalls.Count} tool(s)...";
                            toolResults = await ExecuteToolCallsAsync(toolCalls);
                            RemoveToolProgressMessages();

                            truncated = TruncateToolResults(toolResults);
                            totalChars = GetTotalChars(truncated);
                            StatusMessage = totalChars > AnalyzeThresholdChars ? "Analyzing results..." : "Finalizing...";

                            (response, toolCalls) = await ChatService.ContinueWithToolResultsAsync(truncated, _cts.Token);
                        }

                        if (!string.IsNullOrEmpty(response))
                            Messages.Add(ChatMessage.FromAssistant(response));
                        StatusMessage = "Ready";
                    }
                    catch (OperationCanceledException)
                    {
                        Messages.Add(ChatMessage.FromAssistant("Request was cancelled."));
                        StatusMessage = "Cancelled";
                    }
                    catch (Exception ex)
                    {
                        Messages.Add(ChatMessage.FromAssistant($"Error: {ex.Message}"));
                        StatusMessage = "Error occurred";
                    }
                    finally
                    {
                        RemoveToolProgressMessages();
                        IsBusy = false;
                        _cts?.Dispose();
                        _cts = null;
                    }
                    return;
                }

                if (IsCancelMessage(text))
                {
                    Messages.Add(ChatMessage.FromUser(text));
                    _pendingToolCalls = null;
                    ChatService.RepairHistoryAfterCancel();
                    Messages.Add(ChatMessage.FromAssistant("Okay, cancelled."));
                    InputText = "";
                    StatusMessage = "Cancelled";
                    return;
                }

                _pendingToolCalls = null;
                ChatService.RepairHistoryAfterCancel();
            }

            Messages.Add(ChatMessage.FromUser(text));
            InputText = "";
            IsBusy = true;
            _toolExecutedInSession = false;
            _lastUserPrompt = text;
            _lastToolNames = new List<string>();
            StatusMessage = "Thinking...";

            _cts = new CancellationTokenSource(SendTimeout);

            try
            {
                var contextText = await CollectContextAsync(text);
                var llmText = string.IsNullOrWhiteSpace(contextText)
                    ? text
                    : $"{text}\n\n[Context]\n{contextText}";

                var (response, toolCalls) = await ChatService.SendMessageAsync(llmText, _cts.Token);

                while (toolCalls != null && toolCalls.Count > 0)
                {
                    if (RequiresConfirmation(toolCalls, out var prompt))
                    {
                        _pendingToolCalls = toolCalls;
                        Messages.Add(ChatMessage.FromAssistant(prompt));
                        StatusMessage = "Awaiting confirmation";
                        return;
                    }

                    foreach (var tc in toolCalls)
                        Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));

                    StatusMessage = $"Executing {toolCalls.Count} tool(s)...";

                    var toolResults = await ExecuteToolCallsAsync(toolCalls);
                    _toolExecutedInSession = true;
                    _lastToolNames.AddRange(toolCalls.Select(t => t.FunctionName));
                    RemoveToolProgressMessages();

                    UpdateWorkingMemory(toolCalls, toolResults);
                    var compressed = CompressAndTruncate(toolCalls, toolResults);

                    var totalChars = GetTotalChars(compressed);
                    StatusMessage = totalChars > AnalyzeThresholdChars ? "Analyzing results..." : "Finalizing...";
                    (response, toolCalls) = await ChatService.ContinueWithToolResultsAsync(compressed, _cts.Token);
                }

                if (!string.IsNullOrEmpty(response) && !IsEchoResponse(response))
                    AddAssistantMessage(response);
                else if (_toolExecutedInSession)
                    AddAssistantMessage("Done. The action was completed.");

                StatusMessage = "Ready";
            }
            catch (OperationCanceledException)
            {
                Messages.Add(ChatMessage.FromAssistant("Request was cancelled."));
                StatusMessage = "Cancelled";
            }
            catch (Exception ex)
            {
                Messages.Add(ChatMessage.FromAssistant($"Error: {ex.Message}"));
                StatusMessage = "Error occurred";
            }
            finally
            {
                RemoveToolProgressMessages();
                IsBusy = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private bool CanSend() => !IsBusy;

        private async Task<Dictionary<string, string>> ExecuteToolCallsAsync(List<ToolCallRequest> toolCalls)
        {
            _toolResultsTcs = new TaskCompletionSource<Dictionary<string, string>>();

            using var ctReg = _cts?.Token.Register(() =>
                _toolResultsTcs.TrySetCanceled());

            _queue.Clear();
            _queue.EnqueueAll(toolCalls);
            _externalEvent.Raise();

            var timeoutTask = Task.Delay(ToolTimeoutMs, _cts?.Token ?? CancellationToken.None);
            var completed = await Task.WhenAny(_toolResultsTcs.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                _toolResultsTcs.TrySetCanceled();
                throw new TimeoutException("Tool execution timed out. Revit may be busy or a modal dialog is open.");
            }

            return await _toolResultsTcs.Task;
        }

        private Dictionary<string, string> TruncateToolResults(Dictionary<string, string> results)
        {
            var maxPerResult = MaxToolResultChars;
            var truncated = new Dictionary<string, string>(results.Count);
            foreach (var kvp in results)
            {
                if (kvp.Value != null && kvp.Value.Length > maxPerResult)
                {
                    truncated[kvp.Key] = kvp.Value[..maxPerResult]
                        + $"\n...[TRUNCATED — original {kvp.Value.Length} chars, showing first {maxPerResult}. Summarize what you have.]";
                }
                else
                {
                    truncated[kvp.Key] = kvp.Value;
                }
            }
            return truncated;
        }

        private void HandleToolCallsCompleted(Dictionary<string, string> results)
        {
            InvokeOnDispatcher(() => _toolResultsTcs?.TrySetResult(results));
        }

        private void HandleHandlerError(string error)
        {
            InvokeOnDispatcher(() => _toolResultsTcs?.TrySetException(
                new InvalidOperationException($"Revit handler error: {error}")));
        }

        private void InvokeOnDispatcher(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher ?? _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                action();
                return;
            }
            dispatcher.BeginInvoke(action);
        }

        private void RemoveToolProgressMessages()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsToolCall)
                    Messages.RemoveAt(i);
            }
        }

        private void HandleChatDebug(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            InvokeOnDispatcher(() => StatusMessage = message);
        }

        private static bool IsEchoResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var t = text.Trim();
            if (t.StartsWith("[Executing") || t == "(tool call)" || t == "...")
                return true;
            if (t.Contains("<tool_call>") || t.Contains("</tool_call>"))
                return true;
            if (t.Contains("\"arguments\"") && t.Contains("{"))
                return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\s*[\{\};,"":\[\]]+\s*$"))
                return true;
            return false;
        }

        private static bool IsConfirmMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.Trim().ToLowerInvariant();
            return lower is "yes" or "y" or "ok" or "okay" or "confirm" or "confirmed"
                || lower.Contains("xác nhận") || lower.Contains("đồng ý") || lower.Contains("tiếp tục")
                || lower.Contains("thực hiện") || lower.Contains("làm đi") || lower.Contains("được");
        }

        private static bool IsCancelMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.Trim().ToLowerInvariant();
            return lower is "no" or "n" or "cancel" or "stop"
                || lower.Contains("hủy") || lower.Contains("không") || lower.Contains("dừng");
        }

        private bool RequiresConfirmation(List<ToolCallRequest> toolCalls, out string prompt)
        {
            prompt = null;
            if (toolCalls == null || toolCalls.Count == 0) return false;

            var risky = toolCalls.Where(IsRiskyToolCall).ToList();
            if (risky.Count == 0) return false;

            var list = string.Join(", ", risky.Select(t => t.FunctionName));
            prompt = $"Bạn có muốn thực hiện thao tác sau không? {list}\nTrả lời 'xác nhận' để tiếp tục hoặc 'hủy' để bỏ.";
            return true;
        }

        private static bool IsRiskyToolCall(ToolCallRequest call)
        {
            var riskyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "delete_elements", "set_parameter_value", "rename_elements",
                "move_elements", "copy_elements", "mirror_elements",
                "duplicate_views", "duplicate_sheets"
            };

            if (!riskyTools.Contains(call.FunctionName)) return false;
            if (call.Arguments == null || call.Arguments.Count == 0) return true;

            if (call.Arguments.ContainsKey("element_id")) return false;

            if (call.Arguments.TryGetValue("element_ids", out var idsObj))
            {
                if (idsObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    return je.GetArrayLength() > 20;
                }
                return false;
            }

            return true;
        }

        private static int GetTotalChars(Dictionary<string, string> results)
        {
            if (results == null) return 0;
            int total = 0;
            foreach (var kvp in results)
                total += kvp.Value?.Length ?? 0;
            return total;
        }

        private void UpdateWorkingMemory(List<ToolCallRequest> toolCalls, Dictionary<string, string> results)
        {
            foreach (var tc in toolCalls)
            {
                if (results.TryGetValue(tc.ToolCallId, out var result))
                    _workingMemory.UpdateFromToolResult(tc.FunctionName, result);
            }
        }

        private Dictionary<string, string> CompressAndTruncate(List<ToolCallRequest> toolCalls, Dictionary<string, string> results)
        {
            var compressed = new Dictionary<string, string>(results.Count);
            var maxPerResult = MaxToolResultChars;

            foreach (var kvp in results)
            {
                var toolName = toolCalls.FirstOrDefault(t => t.ToolCallId == kvp.Key)?.FunctionName ?? "unknown";
                var val = WorkingMemory.CompressToolResult(toolName, kvp.Value);

                if (val != null && val.Length > maxPerResult)
                    val = val[..maxPerResult] + $"\n...[TRUNCATED — {kvp.Value?.Length ?? 0} chars total]";

                compressed[kvp.Key] = val;
            }

            return compressed;
        }

        private async Task<string> CollectContextAsync(string userText)
        {
            var lower = userText?.ToLowerInvariant() ?? "";
            var needsView = lower.Contains("current view") || lower.Contains("active view")
                || lower.Contains("view hiện tại") || lower.Contains("view đang mở");
            var needsSelection = lower.Contains("selected") || lower.Contains("selection")
                || lower.Contains("đang chọn") || lower.Contains("được chọn");
            var needsLevels = lower.Contains("level") || lower.Contains("tầng")
                || lower.Contains("cao độ") || lower.Contains("floor");

            if (!needsView && !needsSelection && !needsLevels) return "";

            var calls = new List<ToolCallRequest>();
            if (needsView)
                calls.Add(new ToolCallRequest { ToolCallId = CreateCallId("get_current_view"), FunctionName = "get_current_view", Arguments = new Dictionary<string, object>() });
            if (needsSelection)
                calls.Add(new ToolCallRequest { ToolCallId = CreateCallId("get_current_selection"), FunctionName = "get_current_selection", Arguments = new Dictionary<string, object>() });
            if (needsLevels)
                calls.Add(new ToolCallRequest { ToolCallId = CreateCallId("get_levels_detailed"), FunctionName = "get_levels_detailed", Arguments = new Dictionary<string, object>() });

            if (calls.Count == 0) return "";

            StatusMessage = "Collecting context...";
            var results = await ExecuteToolCallsAsync(calls);

            var sb = new System.Text.StringBuilder();
            foreach (var kvp in results)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                var snippet = kvp.Value.Length > 2000 ? kvp.Value[..2000] + "...[TRUNCATED]" : kvp.Value;
                sb.AppendLine($"{kvp.Key}: {snippet}");
            }

            if (needsLevels && results.Keys.Any(k => k.Contains("get_levels_detailed")))
                sb.AppendLine("IMPORTANT: Use the EXACT level names from the list above when calling tools. Do NOT use the user's approximate name.");

            return sb.ToString().Trim();
        }

        private static string CreateCallId(string funcName)
        {
            return $"pre_{funcName}_{Guid.NewGuid():N}"[..32];
        }

        [RelayCommand]
        private void Cancel()
        {
            _cts?.Cancel();
            _toolResultsTcs?.TrySetCanceled();
            _pendingToolCalls = null;
            StatusMessage = "Cancelling...";
        }

        [RelayCommand]
        private void ClearChat()
        {
            Messages.Clear();
            ChatService.ClearHistory();
            _pendingToolCalls = null;
            _workingMemory.Clear();
            AddWelcomeMessage();
            StatusMessage = "Chat cleared";
        }

        [RelayCommand]
        private void ToggleSettings()
        {
            IsSettingsVisible = !IsSettingsVisible;
        }

        [RelayCommand]
        private void ThumbsUp(ChatMessage message)
        {
            if (message == null || message.Feedback != FeedbackType.None) return;
            message.Feedback = FeedbackType.ThumbsUp;

            var prompt = message.AssociatedPrompt;
            var toolNames = message.AssociatedToolNames;
            if (!string.IsNullOrWhiteSpace(prompt) && toolNames?.Count > 0)
            {
                var tools = toolNames.Select(n => new ToolUsage { Name = n }).ToList();
                ChatFeedbackService.SaveApproved(prompt, tools);
                StatusMessage = "Feedback saved";
            }
        }

        [RelayCommand]
        private void ThumbsDown(ChatMessage message)
        {
            if (message == null || message.Feedback != FeedbackType.None) return;
            message.Feedback = FeedbackType.ThumbsDown;

            var prompt = message.AssociatedPrompt;
            var toolNames = message.AssociatedToolNames;
            if (toolNames?.Count > 0)
            {
                foreach (var toolName in toolNames)
                    ChatFeedbackService.SaveCorrection(prompt, toolName, null);
                StatusMessage = "Correction saved";
            }
        }

        private ChatMessage AddAssistantMessage(string content)
        {
            var msg = ChatMessage.FromAssistant(content);
            msg.AssociatedPrompt = _lastUserPrompt;
            msg.AssociatedToolNames = _lastToolNames.Count > 0 ? new List<string>(_lastToolNames) : null;
            Messages.Add(msg);
            return msg;
        }

        public void Cleanup()
        {
            _handler.OnToolCallsCompleted -= HandleToolCallsCompleted;
            _handler.OnError -= HandleHandlerError;
            if (_diagnosticService != null)
                _diagnosticService.DebugMessage -= HandleChatDebug;
            _toolResultsTcs?.TrySetCanceled();
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
