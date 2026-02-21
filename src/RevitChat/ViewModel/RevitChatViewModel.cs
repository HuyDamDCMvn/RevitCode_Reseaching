using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RevitChat.Handler;
using RevitChat.Models;
using RevitChat.Services;
using RevitChat.Skills;

namespace RevitChat.ViewModel
{
    public partial class RevitChatViewModel : BaseChatViewModel
    {
        private readonly OpenAiChatService _chatService;

        protected override IChatService ChatService => _chatService;
        protected override int ToolTimeoutMs => 60_000;
        protected override TimeSpan SendTimeout => TimeSpan.FromMinutes(3);
        protected override string NotInitializedMessage => "Please set your API key first (click Settings)";
        protected override string WelcomeText => "Hello, I'm HD's Assistant.";

        [ObservableProperty]
        private string _apiKey = "";

        [ObservableProperty]
        private string _selectedModel = "gpt-4o-mini";

        public List<string> AvailableModels { get; } = new()
        {
            "gpt-4o-mini",
            "gpt-4o",
            "gpt-4.1-mini",
            "gpt-4.1",
            "o4-mini"
        };

        public RevitChatViewModel(ExternalEvent externalEvent, RevitChatHandler handler,
            ChatRequestQueue queue, SkillRegistry skillRegistry)
            : base(externalEvent, handler, queue, skillRegistry)
        {
            _chatService = new OpenAiChatService(skillRegistry);
            LoadConfig();
            AddWelcomeMessage();
        }

        private void LoadConfig()
        {
            var config = ConfigService.Load();
            ApiKey = config.ApiKey;
            SelectedModel = config.Model;

            if (ConfigService.HasApiKey())
            {
                _chatService.Initialize(config.ApiKey, config.Model);
                StatusMessage = "Connected to OpenAI";
            }
            else
            {
                StatusMessage = "API key not set - click Settings";
            }
        }

        [RelayCommand]
        private void SaveSettings()
        {
            try
            {
                var config = ConfigService.Load();
                config.ApiKey = ApiKey?.Trim() ?? "";
                config.Model = SelectedModel ?? "gpt-4o-mini";
                ConfigService.Save(config);

                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    _chatService.Initialize(config.ApiKey, config.Model);
                    StatusMessage = "Settings saved - connected to OpenAI";
                }
                else
                {
                    StatusMessage = "API key is empty";
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
