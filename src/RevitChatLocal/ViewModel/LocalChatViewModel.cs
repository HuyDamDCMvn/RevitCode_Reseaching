using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitChat.Handler;
using RevitChat.Models;
using RevitChat.Skills;
using RevitChatLocal.Services;

namespace RevitChatLocal.ViewModel
{
    public partial class LocalChatViewModel : ObservableObject
    {
        private readonly ExternalEvent _externalEvent;
        private readonly RevitChatHandler _handler;
        private readonly ChatRequestQueue _queue;
        private readonly SkillRegistry _skillRegistry;
        private readonly OllamaChatService _chatService;

        private CancellationTokenSource _cts;
        private TaskCompletionSource<Dictionary<string, string>> _toolResultsTcs;

        public ObservableCollection<ChatMessage> Messages { get; } = new();

        [ObservableProperty]
        private string _inputText = "";

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [ObservableProperty]
        private bool _isSettingsVisible;

        [ObservableProperty]
        private string _endpointUrl = "http://localhost:11434";

        [ObservableProperty]
        private string _selectedModel = "qwen2.5:7b";

        public List<string> AvailableModels { get; } = new()
        {
            "qwen2.5:7b",
            "qwen2.5:14b",
            "qwen2.5-coder:7b",
            "llama3.1:8b",
            "mistral:7b",
        };

        public SkillRegistry SkillRegistry => _skillRegistry;

        public LocalChatViewModel(ExternalEvent externalEvent, RevitChatHandler handler,
            ChatRequestQueue queue, SkillRegistry skillRegistry)
        {
            _externalEvent = externalEvent;
            _handler = handler;
            _queue = queue;
            _skillRegistry = skillRegistry;
            _chatService = new OllamaChatService(skillRegistry);

            _handler.OnToolCallsCompleted += HandleToolCallsCompleted;
            _handler.OnError += HandleHandlerError;

            LoadConfig();
            AddWelcomeMessage();
        }

        private void LoadConfig()
        {
            var config = LocalConfigService.Load();
            EndpointUrl = config.EndpointUrl;
            SelectedModel = config.Model;

            if (LocalConfigService.HasEndpoint())
            {
                try
                {
                    _chatService.Initialize(config.EndpointUrl, config.Model);
                    StatusMessage = $"Connected to Ollama ({config.Model})";
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Connection failed: {ex.Message}";
                }
            }
            else
            {
                StatusMessage = "Endpoint not set - click Settings";
            }
        }

        private void AddWelcomeMessage()
        {
            Messages.Add(ChatMessage.FromAssistant(
                "Hello! I'm your Revit AI Assistant (Local).\n\n" +
                "Powered by Ollama - runs locally, free, no API key needed.\n\n" +
                "I can help you:\n" +
                "- Query & search elements, parameters\n" +
                "- MEP: systems, equipment, spaces, airflow\n" +
                "- MEP: quantity takeoff, insulation, hangers\n" +
                "- MEP: validation, disconnected, warnings\n" +
                "- Modify parameters, select, delete\n" +
                "- Export data to CSV / BOQ\n\n" +
                "Ask me anything about your Revit model!"));
        }

        [RelayCommand(CanExecute = nameof(CanSend))]
        private async Task SendAsync()
        {
            var text = InputText?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (!_chatService.IsInitialized)
            {
                StatusMessage = "Please configure Ollama endpoint (click Settings)";
                IsSettingsVisible = true;
                return;
            }

            Messages.Add(ChatMessage.FromUser(text));
            InputText = "";
            IsBusy = true;
            StatusMessage = "Thinking...";

            _cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            try
            {
                var (response, toolCalls) = await _chatService.SendMessageAsync(text, _cts.Token);

                while (toolCalls != null && toolCalls.Count > 0)
                {
                    foreach (var tc in toolCalls)
                    {
                        Messages.Add(ChatMessage.ToolProgress(tc.FunctionName));
                    }
                    StatusMessage = $"Executing {toolCalls.Count} tool(s)...";

                    var toolResults = await ExecuteToolCallsAsync(toolCalls);
                    RemoveToolProgressMessages();

                    StatusMessage = "Analyzing results...";
                    (response, toolCalls) = await _chatService.ContinueWithToolResultsAsync(toolResults, _cts.Token);
                }

                if (!string.IsNullOrEmpty(response))
                {
                    Messages.Add(ChatMessage.FromAssistant(response));
                }

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

            _queue.Clear();
            _queue.EnqueueAll(toolCalls);
            _externalEvent.Raise();

            return await _toolResultsTcs.Task;
        }

        private void HandleToolCallsCompleted(Dictionary<string, string> results)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _toolResultsTcs?.TrySetResult(results);
            }));
        }

        private void HandleHandlerError(string error)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _toolResultsTcs?.TrySetException(
                    new InvalidOperationException($"Revit handler error: {error}"));
            }));
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
            StatusMessage = "Cancelling...";
        }

        [RelayCommand]
        private void ClearChat()
        {
            Messages.Clear();
            _chatService.ClearHistory();
            AddWelcomeMessage();
            StatusMessage = "Chat cleared";
        }

        [RelayCommand]
        private void ToggleSettings()
        {
            IsSettingsVisible = !IsSettingsVisible;
        }

        [RelayCommand]
        private void SaveSettings()
        {
            try
            {
                var config = new OllamaConfig
                {
                    EndpointUrl = EndpointUrl?.Trim() ?? "http://localhost:11434",
                    Model = SelectedModel ?? "qwen2.5:7b"
                };
                LocalConfigService.Save(config);

                if (!string.IsNullOrWhiteSpace(config.EndpointUrl))
                {
                    _chatService.Initialize(config.EndpointUrl, config.Model);
                    StatusMessage = $"Settings saved - connected to Ollama ({config.Model})";
                }
                else
                {
                    StatusMessage = "Endpoint URL is empty";
                }

                IsSettingsVisible = false;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to save: {ex.Message}";
            }
        }

        public void Cleanup()
        {
            _handler.OnToolCallsCompleted -= HandleToolCallsCompleted;
            _handler.OnError -= HandleHandlerError;
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
