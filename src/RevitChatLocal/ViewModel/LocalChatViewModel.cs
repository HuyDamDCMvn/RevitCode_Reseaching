using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitChat.Handler;
using RevitChat.Models;
using RevitChat.Skills;
using RevitChat.ViewModel;
using RevitChatLocal.Services;

namespace RevitChatLocal.ViewModel
{
    public partial class LocalChatViewModel : BaseChatViewModel
    {
        private readonly OllamaChatService _chatService;

        protected override IChatService ChatService => _chatService;
        protected override int ToolTimeoutMs => 120_000;
        protected override TimeSpan SendTimeout => TimeSpan.FromMinutes(5);
        protected override string NotInitializedMessage => "Please configure Ollama endpoint (click Settings)";
        protected override string WelcomeText =>
            "Hello! I'm your Revit AI Assistant (Local).\n\n" +
            "Powered by Ollama - runs locally, free, no API key needed.\n\n" +
            "I can help you:\n" +
            "- Query & search elements, parameters\n" +
            "- MEP: systems, equipment, spaces, airflow\n" +
            "- MEP: quantity takeoff, insulation, hangers\n" +
            "- MEP: validation, disconnected, warnings\n" +
            "- Modify parameters, select, delete\n" +
            "- Export data to CSV / BOQ\n\n" +
            "Ask me anything about your Revit model!";

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

        public LocalChatViewModel(ExternalEvent externalEvent, RevitChatHandler handler,
            ChatRequestQueue queue, SkillRegistry skillRegistry)
            : base(externalEvent, handler, queue, skillRegistry)
        {
            _chatService = new OllamaChatService(skillRegistry);
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

        [RelayCommand]
        private void SaveSettings()
        {
            try
            {
                var config = LocalConfigService.Load();
                config.EndpointUrl = EndpointUrl?.Trim() ?? "http://localhost:11434";
                config.Model = SelectedModel ?? "qwen2.5:7b";
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
    }
}
