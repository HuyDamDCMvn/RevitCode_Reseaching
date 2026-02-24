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
        private readonly ToolExecutionService _toolExec;
        private readonly ContextCollectionService _contextService = new();
        private readonly ConversationContextTracker _contextTracker = new();
        private readonly Dispatcher _dispatcher;

        private CancellationTokenSource _cts;
        private List<ToolCallRequest> _pendingToolCalls;
        private IChatService _diagnosticService;
        private bool _toolExecutedInSession;
        private string _lastUserPrompt;
        private PromptContext _promptContext;
        private List<string> _lastToolNames = new();
        private List<ToolCallRequest> _lastToolCalls = new();
        private readonly Action _onModelModifiedHandler;
        private InteractionRecord _currentInteraction;
        private Dictionary<string, string> _lastToolResults;
        private System.Diagnostics.Stopwatch _responseTimer;

        private const int AnalyzeThresholdChars = 1200;
        private ChatMessage _streamingMessage;

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

        private readonly SkillRegistry _skillRegistry;
        public SkillRegistry SkillRegistry => _skillRegistry;

        protected BaseChatViewModel(ExternalEvent externalEvent, RevitChatHandler handler,
            ChatRequestQueue queue, SkillRegistry skillRegistry)
        {
            _skillRegistry = skillRegistry;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _toolExec = new ToolExecutionService(externalEvent, handler, queue);
            _onModelModifiedHandler = () => _contextService.InvalidateCache();
            _toolExec.OnModelModified += _onModelModifiedHandler;
        }

        #region Streaming

        private void BeginStreaming()
        {
            _streamingMessage = null;
            ChatService.TokenReceived += OnTokenReceived;
        }

        private void EndStreaming()
        {
            ChatService.TokenReceived -= OnTokenReceived;
        }

        private void OnTokenReceived(string token)
        {
            InvokeOnDispatcher(() =>
            {
                if (_streamingMessage == null)
                {
                    _streamingMessage = ChatMessage.FromAssistant(token);
                    Messages.Add(_streamingMessage);
                }
                else
                {
                    _streamingMessage.Content += token;
                }
            });
        }

        #endregion

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
                var dynamicExamples = DynamicFewShotSelector.ExampleCount;
                var adaptiveWeights = AdaptiveWeightManager.TotalAdjustments;
                int total = approved + corrections + dynamicExamples + adaptiveWeights;
                if (total > 0)
                    msg += $"\n({approved} patterns, {dynamicExamples} learned examples, {adaptiveWeights} adapted weights)";
            }
            catch { }
            Messages.Add(ChatMessage.FromAssistant(msg));
        }

        protected virtual int MaxToolResultChars => 4000;

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
                if (ChatGuardService.IsConfirmMessage(text))
                {
                    await HandleConfirmationAsync(text);
                    return;
                }

                if (ChatGuardService.IsCancelMessage(text))
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

            // Phase 1b: Check if this is a user correction before processing
            var correction = UserCorrectionCapture.Detect(text, _lastUserPrompt, _lastToolNames);
            if (correction is { IsCorrection: true })
            {
                UserCorrectionCapture.SaveCorrection(correction);
                if (_currentInteraction != null)
                    InteractionLogger.MarkRetried(_currentInteraction);
            }

            Messages.Add(ChatMessage.FromUser(text));
            InputText = "";
            IsBusy = true;
            _toolExecutedInSession = false;
            _lastUserPrompt = text;
            _lastToolNames = new List<string>();
            _lastToolCalls = new List<ToolCallRequest>();
            _lastToolResults = null;
            _responseTimer = System.Diagnostics.Stopwatch.StartNew();
            StatusMessage = "Thinking...";

            _cts = new CancellationTokenSource(SendTimeout);

            try
            {
                await OnBeforeSendAsync(_cts.Token);
                StatusMessage = $"Collecting context... ({_responseTimer.Elapsed.TotalSeconds:F1}s)";
                _promptContext = PromptAnalyzer.Analyze(text);
                _contextTracker.ApplyCarryover(_promptContext, text);
                _contextTracker.Update(_promptContext);
                _currentInteraction = InteractionLogger.BeginInteraction(text, _promptContext);
                var contextText = await _contextService.CollectAsync(text,
                    calls => _toolExec.ExecuteAsync(calls, ToolTimeoutMs, _cts.Token));
                var llmText = string.IsNullOrWhiteSpace(contextText)
                    ? text
                    : $"{text}\n\n[Context]\n{contextText}";

                StatusMessage = $"Thinking... ({_responseTimer.Elapsed.TotalSeconds:F1}s)";
                BeginStreaming();
                try
                {
                    var (response, toolCalls) = await ChatService.SendMessageAsync(llmText, _cts.Token);
                    EndStreaming();
                    (response, toolCalls) = await ValidateAndRetryIfNeeded(response, toolCalls);

                    if (_streamingMessage != null && toolCalls != null && toolCalls.Count > 0)
                    {
                        Messages.Remove(_streamingMessage);
                        _streamingMessage = null;
                    }

                    await ProcessToolCallLoopAsync(response, toolCalls);
                }
                catch
                {
                    EndStreaming();
                    _streamingMessage = null;
                    throw;
                }

                InteractionLogger.EndInteraction(_currentInteraction, true);
                RecordLearningSignals(text, true);
            }
            catch (OperationCanceledException)
            {
                Messages.Add(ChatMessage.FromAssistant("Request was cancelled."));
                StatusMessage = "Cancelled";
                InteractionLogger.EndInteraction(_currentInteraction, false, "cancelled");
            }
            catch (Exception ex)
            {
                Messages.Add(ChatMessage.FromAssistant($"Error: {ex.Message}"));
                StatusMessage = "Error occurred";
                InteractionLogger.EndInteraction(_currentInteraction, false, ex.Message);
                RecordLearningSignals(text, false);
            }
            finally
            {
                _responseTimer?.Stop();
                var elapsed = _responseTimer?.Elapsed;
                RemoveToolProgressMessages();
                IsBusy = false;
                StatusMessage = elapsed != null
                    ? $"Ready — {elapsed.Value.TotalSeconds:F1}s"
                    : "Ready";
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task HandleConfirmationAsync(string text)
        {
            Messages.Add(ChatMessage.FromUser(text));
            InputText = "";
            IsBusy = true;
            _responseTimer = System.Diagnostics.Stopwatch.StartNew();
            StatusMessage = "Executing confirmed action...";
            _cts = new CancellationTokenSource(SendTimeout);

            try
            {
                var confirmedCalls = _pendingToolCalls;
                _pendingToolCalls = null;

                foreach (var tc in confirmedCalls)
                    Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));

                var ts = _responseTimer?.Elapsed.TotalSeconds ?? 0;
                StatusMessage = $"Executing {confirmedCalls.Count} tool(s)... ({ts:F1}s)";
                var toolResults = await _toolExec.ExecuteAsync(confirmedCalls, ToolTimeoutMs, _cts.Token);
                _toolExecutedInSession = true;
                _lastToolNames.AddRange(confirmedCalls.Select(t => t.FunctionName));
                _lastToolCalls.AddRange(confirmedCalls);
                RemoveToolProgressMessages();

                _toolExec.UpdateMemory(confirmedCalls, toolResults);
                var compressed = ToolExecutionService.CompressAndTruncate(confirmedCalls, toolResults, MaxToolResultChars);
                var totalChars = ChatGuardService.GetTotalChars(compressed);
                ts = _responseTimer?.Elapsed.TotalSeconds ?? 0;
                StatusMessage = totalChars > AnalyzeThresholdChars
                    ? $"Analyzing results... ({ts:F1}s)"
                    : $"Finalizing... ({ts:F1}s)";

                var (response, toolCalls) = await ChatService.ContinueWithToolResultsAsync(compressed, _cts.Token);
                await ProcessToolCallLoopAsync(response, toolCalls);

                _lastToolResults = toolResults;
                RecordLearningSignals(text, true);
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
                RecordLearningSignals(text, false);
            }
            finally
            {
                _responseTimer?.Stop();
                var elapsed = _responseTimer?.Elapsed;
                RemoveToolProgressMessages();
                IsBusy = false;
                StatusMessage = elapsed != null
                    ? $"Ready — {elapsed.Value.TotalSeconds:F1}s"
                    : "Ready";
                _cts?.Dispose();
                _cts = null;
            }
        }

        private async Task ProcessToolCallLoopAsync(string response, List<ToolCallRequest> toolCalls)
        {
            while (toolCalls != null && toolCalls.Count > 0)
            {
                // Auto-dry-run preview runs BEFORE confirmation so bulk ops show impact first
                var autoDryRun = toolCalls.Where(ChatGuardService.ShouldAutoDryRun).ToList();
                if (autoDryRun.Count > 0)
                {
                    var previewCalls = autoDryRun.Select(ChatGuardService.CloneWithDryRun).ToList();
                    StatusMessage = "Running preview...";
                    var previewResults = await _toolExec.ExecuteAsync(previewCalls, ToolTimeoutMs, _cts.Token);
                    var previewText = string.Join("\n", previewResults.Values.Take(3).Select(v =>
                        v.Length > 500 ? v[..500] + "..." : v));
                    Messages.Add(ChatMessage.FromAssistant($"Preview of changes:\n{previewText}\n\nConfirm to proceed or cancel."));
                    _pendingToolCalls = toolCalls;
                    StatusMessage = "Awaiting confirmation";
                    return;
                }

                if (ChatGuardService.RequiresConfirmation(toolCalls, out var prompt))
                {
                    _pendingToolCalls = toolCalls;
                    Messages.Add(ChatMessage.FromAssistant(prompt));
                    StatusMessage = "Awaiting confirmation";
                    return;
                }

                ToolCallEnricher.Enrich(toolCalls, _promptContext);

                foreach (var tc in toolCalls)
                    Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));

                var ts = _responseTimer?.Elapsed.TotalSeconds ?? 0;
                StatusMessage = $"Executing {toolCalls.Count} tool(s)... ({ts:F1}s)";
                var toolResults = await _toolExec.ExecuteAsync(toolCalls, ToolTimeoutMs, _cts.Token);
                _toolExecutedInSession = true;
                _lastToolNames.AddRange(toolCalls.Select(t => t.FunctionName));
                _lastToolCalls.AddRange(toolCalls);
                _lastToolResults = toolResults;
                InteractionLogger.RecordToolCalls(_currentInteraction, toolCalls, toolResults);
                RemoveToolProgressMessages();

                _toolExec.UpdateMemory(toolCalls, toolResults);
                var compressed = ToolExecutionService.CompressAndTruncate(toolCalls, toolResults, MaxToolResultChars);
                var totalChars = ChatGuardService.GetTotalChars(compressed);
                ts = _responseTimer?.Elapsed.TotalSeconds ?? 0;
                StatusMessage = totalChars > AnalyzeThresholdChars
                    ? $"Analyzing results... ({ts:F1}s)"
                    : $"Finalizing... ({ts:F1}s)";

                BeginStreaming();
                try
                {
                    (response, toolCalls) = await ChatService.ContinueWithToolResultsAsync(compressed, _cts.Token);
                    EndStreaming();
                    (response, toolCalls) = await ValidateAndRetryIfNeeded(response, toolCalls);

                    if (_streamingMessage != null && toolCalls != null && toolCalls.Count > 0)
                    {
                        Messages.Remove(_streamingMessage);
                        _streamingMessage = null;
                    }
                }
                catch
                {
                    EndStreaming();
                    _streamingMessage = null;
                    throw;
                }
            }

            var timingFooter = _responseTimer != null
                ? $"\n\n⏱ {_responseTimer.Elapsed.TotalSeconds:F1}s"
                : "";

            if (_streamingMessage != null)
            {
                if (IsGarbageResponse(_streamingMessage.Content))
                {
                    Messages.Remove(_streamingMessage);
                    _streamingMessage = null;
                    if (_toolExecutedInSession)
                        AddAssistantMessage($"Done. The action was completed.{timingFooter}");
                }
                else
                {
                    _streamingMessage.Content += timingFooter;
                    _streamingMessage.AssociatedPrompt = _lastUserPrompt;
                    _streamingMessage.AssociatedToolNames = _lastToolNames.Count > 0 ? new List<string>(_lastToolNames) : null;
                    _streamingMessage.AssociatedToolCalls = _lastToolCalls.Count > 0 ? new List<ToolCallRequest>(_lastToolCalls) : null;
                }
            }
            else if (!string.IsNullOrEmpty(response) && !ChatGuardService.IsEchoResponse(response))
                AddAssistantMessage(response + timingFooter);
            else if (_toolExecutedInSession)
                AddAssistantMessage($"Done. The action was completed.{timingFooter}");

            StatusMessage = "Ready";
        }

        private bool CanSend() => !IsBusy;

        /// <summary>Override to run pre-send work (e.g. wait for model warmup).</summary>
        protected virtual Task OnBeforeSendAsync(CancellationToken ct) => Task.CompletedTask;

        private async Task<(string response, List<ToolCallRequest> toolCalls)> ValidateAndRetryIfNeeded(
            string response, List<ToolCallRequest> toolCalls)
        {
            if (toolCalls == null || toolCalls.Count == 0) return (response, toolCalls);

            var errors = ChatService.ValidateToolCalls(toolCalls);
            if (errors.Count == 0) return (response, toolCalls);

            StatusMessage = "Fixing invalid tool call...";
            return await ChatService.RetryWithValidationErrorAsync(errors, _cts.Token);
        }

        private void InvokeOnDispatcher(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher ?? _dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return; // skip UI updates when dispatcher is unavailable or shutting down
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

        private static bool IsGarbageResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return true;
            var trimmed = content.Trim().ToLowerInvariant();
            if (trimmed.Length < 5) return true;
            if (trimmed.Contains("tool_call") || trimmed.Contains("tool call")) return true;
            if (trimmed.StartsWith("[calling") || trimmed.StartsWith("(calling")) return true;
            if (trimmed == "null" || trimmed == "none" || trimmed == "n/a") return true;
            return false;
        }

        private void HandleChatDebug(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            InvokeOnDispatcher(() => StatusMessage = message);
        }

        [RelayCommand]
        private void Cancel()
        {
            _cts?.Cancel();
            _toolExec.CancelPending();
            _pendingToolCalls = null;
            StatusMessage = "Cancelling...";
        }

        [RelayCommand]
        private void ClearChat()
        {
            Messages.Clear();
            ChatService.ClearHistory();
            _contextTracker.Reset();
            _pendingToolCalls = null;
            AddWelcomeMessage();
            StatusMessage = "Chat cleared";
        }

        [RelayCommand]
        private void ToggleSettings()
        {
            IsSettingsVisible = !IsSettingsVisible;
        }

        [RelayCommand]
        private async Task RetrainAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            StatusMessage = "Self-training...";

            try
            {
                var report = await Task.Run(() => SelfTrainingService.RunTraining(epochs: 3, augment: true));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Self-training complete ({report.Epochs} epochs × {report.TotalSamples} samples):");
                sb.AppendLine($"Overall: {report.Matched}/{report.TotalProcessed} ({report.OverallAccuracy:F0}%)");
                sb.AppendLine($"Intent: {report.IntentAccuracy:F0}% | Category: {report.CategoryAccuracy:F0}% | Tool: {report.ToolAccuracy:F0}%");
                sb.AppendLine($"Conv: {report.ConversationMatched}/{report.ConversationSteps} ({report.ConversationAccuracy:F0}%) [{report.ConversationChains} chains]");
                sb.AppendLine($"{report.FewShotAdded} examples, {report.WeightsAdjusted} weights ({report.ElapsedMs}ms)");
                if (report.EpochResults.Count > 1)
                {
                    sb.Append("Per-epoch: ");
                    foreach (var er in report.EpochResults)
                        sb.Append($"E{er.Epoch}={er.Matched} ");
                }
                Messages.Add(ChatMessage.FromAssistant(sb.ToString().TrimEnd()));
                StatusMessage = "Training complete";
            }
            catch (Exception ex)
            {
                Messages.Add(ChatMessage.FromAssistant($"Training error: {ex.Message}"));
                StatusMessage = "Training failed";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void ThumbsUp(ChatMessage message)
        {
            if (message == null || message.Feedback != FeedbackType.None) return;
            message.Feedback = FeedbackType.ThumbsUp;

            var prompt = message.AssociatedPrompt;
            var toolCalls = message.AssociatedToolCalls;
            var toolNames = message.AssociatedToolNames;
            if (!string.IsNullOrWhiteSpace(prompt) && (toolCalls?.Count > 0 || toolNames?.Count > 0))
            {
                List<ToolUsage> tools;
                if (toolCalls?.Count > 0)
                {
                    tools = toolCalls.Select(tc => new ToolUsage
                    {
                        Name = tc.FunctionName,
                        Args = tc.Arguments ?? new Dictionary<string, object>()
                    }).ToList();
                }
                else
                {
                    tools = toolNames.Select(n => new ToolUsage { Name = n }).ToList();
                }
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
            msg.AssociatedToolCalls = _lastToolCalls.Count > 0 ? new List<ToolCallRequest>(_lastToolCalls) : null;
            Messages.Add(msg);
            return msg;
        }

        private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","of","in","on","to","for","and","or","is","are","all","my","me","it",
            "this","that","with","by","from","at","please","can","how","what","show","list",
            "toi","cua","cac","va","cho","trong","la","co","khong","voi","hay","giup",
            "hãy","được","không","bao","nhiêu","tất","cả","mọi","những"
        };

        private static string ExtractMainKeyword(string prompt)
        {
            var words = prompt.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words
                .Where(w => w.Length > 2 && !_stopWords.Contains(w))
                .OrderByDescending(w => w.Length)
                .FirstOrDefault() ?? "";
        }

        private void RecordLearningSignals(string prompt, bool success)
        {
            try
            {
                if (_lastToolCalls == null || _lastToolCalls.Count == 0) return;
                var intent = _promptContext?.PrimaryIntent.ToString() ?? "Unknown";
                var category = _promptContext?.DetectedCategory;

                var keyword = ExtractMainKeyword(prompt);
                foreach (var tc in _lastToolCalls)
                {
                    bool toolSuccess = success && (_lastToolResults == null ||
                        (_lastToolResults.TryGetValue(tc.ToolCallId ?? tc.FunctionName, out var r) &&
                         !string.IsNullOrEmpty(r) && !r.Contains("\"error\"")));

                    if (toolSuccess)
                    {
                        DynamicFewShotSelector.RecordSuccess(prompt, tc.FunctionName, tc.Arguments, intent, category);
                        AdaptiveWeightManager.RecordSuccess(intent, keyword, tc.FunctionName);
                        ProjectContextMemory.RecordToolUsage(tc.FunctionName, category,
                            _promptContext?.DetectedSystem, intent);

                        if (ChatService is Models.IEmbeddingCapable embeddable)
                        {
                            _ = embeddable.StoreEmbeddingAsync(prompt, tc.FunctionName,
                                tc.Arguments, intent, CancellationToken.None);
                        }
                    }
                    else
                    {
                        AdaptiveWeightManager.RecordFailure(intent, keyword);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RecordLearningSignals] {ex.Message}");
            }
        }

        public void Cleanup()
        {
            if (_diagnosticService != null)
                _diagnosticService.DebugMessage -= HandleChatDebug;
            if (_onModelModifiedHandler != null)
                _toolExec.OnModelModified -= _onModelModifiedHandler;
            EndStreaming();
            _streamingMessage = null;
            _toolExec.Cleanup();
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
