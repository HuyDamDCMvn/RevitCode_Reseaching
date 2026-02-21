using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitChat.Handler;
using RevitChat.Models;
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

        protected void AddWelcomeMessage()
        {
            Messages.Add(ChatMessage.FromAssistant(WelcomeText));
        }

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

            Messages.Add(ChatMessage.FromUser(text));
            InputText = "";
            IsBusy = true;
            StatusMessage = "Thinking...";

            _cts = new CancellationTokenSource(SendTimeout);

            try
            {
                var (response, toolCalls) = await ChatService.SendMessageAsync(text, _cts.Token);

                while (toolCalls != null && toolCalls.Count > 0)
                {
                    foreach (var tc in toolCalls)
                        Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));

                    StatusMessage = $"Executing {toolCalls.Count} tool(s)...";

                    var toolResults = await ExecuteToolCallsAsync(toolCalls);
                    RemoveToolProgressMessages();

                    StatusMessage = "Analyzing results...";
                    (response, toolCalls) = await ChatService.ContinueWithToolResultsAsync(toolResults, _cts.Token);
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

        [RelayCommand]
        private void Cancel()
        {
            _cts?.Cancel();
            _toolResultsTcs?.TrySetCanceled();
            StatusMessage = "Cancelling...";
        }

        [RelayCommand]
        private void ClearChat()
        {
            Messages.Clear();
            ChatService.ClearHistory();
            AddWelcomeMessage();
            StatusMessage = "Chat cleared";
        }

        [RelayCommand]
        private void ToggleSettings()
        {
            IsSettingsVisible = !IsSettingsVisible;
        }

        public void Cleanup()
        {
            _handler.OnToolCallsCompleted -= HandleToolCallsCompleted;
            _handler.OnError -= HandleHandlerError;
            _toolResultsTcs?.TrySetCanceled();
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
