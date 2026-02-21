using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        private CancellationTokenSource _pullCts;

        protected override IChatService ChatService => _chatService;
        protected override int ToolTimeoutMs => 120_000;
        protected override TimeSpan SendTimeout => TimeSpan.FromMinutes(5);
        protected override string NotInitializedMessage => "Please configure Ollama endpoint (click Settings)";
        protected override string WelcomeText => "Hello, I'm HD's Assistant.";
        protected override int MaxToolResultChars => 4000;

        [ObservableProperty]
        private string _endpointUrl = "http://localhost:11434";

        [ObservableProperty]
        private string _selectedModel = "qwen2.5:7b";

        // Tool selection mode
        [ObservableProperty]
        private bool _isModeSmart = true;

        [ObservableProperty]
        private bool _isModeTwoStage;

        [ObservableProperty]
        private bool _isModeShowAll;

        // Skill packs
        [ObservableProperty]
        private bool _isPackViewControl = true;

        [ObservableProperty]
        private bool _isPackMep = true;

        [ObservableProperty]
        private bool _isPackModeler = true;

        [ObservableProperty]
        private bool _isPackBimCoord = true;

        [ObservableProperty]
        private bool _isPackLinked = true;

        // Model management
        [ObservableProperty]
        private bool _isPulling;

        [ObservableProperty]
        private string _pullProgress = "";

        public ObservableCollection<string> AvailableModels { get; } = new();

        public ObservableCollection<string> InstalledModels { get; } = new();

        public LocalChatViewModel(ExternalEvent externalEvent, RevitChatHandler handler,
            ChatRequestQueue queue, SkillRegistry skillRegistry)
            : base(externalEvent, handler, queue, skillRegistry)
        {
            _chatService = new OllamaChatService(skillRegistry);

            foreach (var m in OllamaModelService.RecommendedModels)
                AvailableModels.Add(m);

            LoadConfig();
            AddWelcomeMessage();
            _ = RefreshModelsAsync();
        }

        private void LoadConfig()
        {
            var config = LocalConfigService.Load();
            EndpointUrl = config.EndpointUrl;
            SelectedModel = config.Model;

            switch (config.ToolSelectionMode)
            {
                case "twostage": IsModeTwoStage = true; IsModeSmart = false; break;
                case "showall": IsModeShowAll = true; IsModeSmart = false; break;
                default: IsModeSmart = true; break;
            }

            var packs = config.EnabledSkillPacks ?? new List<string>();
            IsPackViewControl = packs.Contains("ViewControl");
            IsPackMep = packs.Contains("MEP");
            IsPackModeler = packs.Contains("Modeler");
            IsPackBimCoord = packs.Contains("BIMCoordinator");
            IsPackLinked = packs.Contains("LinkedModels");

            if (LocalConfigService.HasEndpoint())
            {
                try
                {
                    _chatService.Initialize(config.EndpointUrl, config.Model);
                    ApplyToolSettings(config);
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

        private string GetSelectedMode()
        {
            if (IsModeTwoStage) return "twostage";
            if (IsModeShowAll) return "showall";
            return "smart";
        }

        private List<string> GetEnabledPacks()
        {
            var packs = new List<string> { "Core" };
            if (IsPackViewControl) packs.Add("ViewControl");
            if (IsPackMep) packs.Add("MEP");
            if (IsPackModeler) packs.Add("Modeler");
            if (IsPackBimCoord) packs.Add("BIMCoordinator");
            if (IsPackLinked) packs.Add("LinkedModels");
            return packs;
        }

        private void ApplyToolSettings(OllamaConfig config)
        {
            _chatService.SetToolMode(config.ToolSelectionMode ?? "smart");
            _chatService.SetEnabledPacks(config.EnabledSkillPacks ?? new List<string>
            {
                "Core", "ViewControl", "MEP", "Modeler", "BIMCoordinator", "LinkedModels"
            });
        }

        [RelayCommand]
        private void SaveSettings()
        {
            try
            {
                var config = LocalConfigService.Load();
                config.EndpointUrl = EndpointUrl?.Trim() ?? "http://localhost:11434";
                config.Model = SelectedModel ?? "qwen2.5:7b";
                config.ToolSelectionMode = GetSelectedMode();
                config.EnabledSkillPacks = GetEnabledPacks();
                LocalConfigService.Save(config);

                if (!string.IsNullOrWhiteSpace(config.EndpointUrl))
                {
                    _chatService.Initialize(config.EndpointUrl, config.Model);
                    ApplyToolSettings(config);
                    StatusMessage = $"Saved - {config.Model} / {config.ToolSelectionMode} mode";
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

        [RelayCommand]
        private async Task RefreshModelsAsync()
        {
            try
            {
                var endpoint = EndpointUrl?.Trim() ?? "http://localhost:11434";
                var models = await OllamaModelService.GetModelListWithStatusAsync(endpoint);

                var currentSelection = SelectedModel;
                var newNames = new List<string>();
                var newInstalled = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var m in models)
                {
                    if (seen.Add(m.Name))
                        newNames.Add(m.Name);
                    if (m.IsInstalled)
                        newInstalled.Add($"{m.Name}  [{m.Size}]");
                }

                if (!string.IsNullOrEmpty(currentSelection) && !seen.Contains(currentSelection))
                    newNames.Add(currentSelection);

                AvailableModels.Clear();
                foreach (var n in newNames)
                    AvailableModels.Add(n);

                InstalledModels.Clear();
                foreach (var n in newInstalled)
                    InstalledModels.Add(n);

                SelectedModel = currentSelection;
            }
            catch
            {
            }
        }

        [RelayCommand]
        private async Task PullModelAsync()
        {
            var modelName = SelectedModel?.Trim();
            if (string.IsNullOrEmpty(modelName)) return;
            if (IsPulling) return;

            IsPulling = true;
            PullProgress = $"Starting pull: {modelName}...";
            _pullCts = new CancellationTokenSource();

            try
            {
                var endpoint = EndpointUrl?.Trim() ?? "http://localhost:11434";

                await OllamaModelService.PullModelAsync(
                    endpoint,
                    modelName,
                    progress =>
                    {
                        Application.Current?.Dispatcher?.BeginInvoke(
                            new Action(() => PullProgress = progress));
                    },
                    _pullCts.Token);

                PullProgress = $"{modelName} pulled successfully!";
                await RefreshModelsAsync();
            }
            catch (OperationCanceledException)
            {
                PullProgress = "Pull cancelled";
            }
            catch (Exception ex)
            {
                PullProgress = $"Pull failed: {ex.Message}";
            }
            finally
            {
                IsPulling = false;
                _pullCts?.Dispose();
                _pullCts = null;
            }
        }

        [RelayCommand]
        private void CancelPull()
        {
            _pullCts?.Cancel();
        }
    }
}
