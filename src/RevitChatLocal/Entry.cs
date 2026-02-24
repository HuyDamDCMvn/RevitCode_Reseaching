using System;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using RevitChat.Handler;
using RevitChat.Services;
using RevitChat.Skills;
using RevitChatLocal.UI;
using RevitChatLocal.ViewModel;

namespace RevitChatLocal
{
    public static class Entry
    {
        private static LocalChatWindow _window;
        private static ExternalEvent _externalEvent;
        private static RevitChatHandler _handler;
        private static ChatRequestQueue _queue;
        private static SkillRegistry _skillRegistry;

        public static void ShowTool(UIApplication uiapp)
        {
            if (uiapp == null)
            {
                TaskDialog.Show("RevitChatLocal Error", "UIApplication is null. Cannot open chat window.");
                return;
            }

            try
            {
                if (_window != null && _window.IsVisible)
                {
                    _window.Activate();
                    return;
                }

                var uidoc = uiapp.ActiveUIDocument;
                if (uidoc?.Document == null)
                {
                    TaskDialog.Show("RevitChatLocal", "Please open a Revit project first.");
                    return;
                }

                var dllDir = System.IO.Path.GetDirectoryName(typeof(Entry).Assembly.Location)
                            ?? AppContext.BaseDirectory;
                ChatFeedbackService.Initialize(dllDir);
                InteractionLogger.Initialize(dllDir);
                AdaptiveWeightManager.Initialize(dllDir);
                DynamicFewShotSelector.Initialize(dllDir);
                ProjectContextMemory.Initialize(dllDir);

                var docTitle = uidoc.Document?.Title;
                if (!string.IsNullOrEmpty(docTitle))
                {
                    InteractionLogger.SetProjectContext(docTitle);
                    ProjectContextMemory.SetProject(docTitle);
                }

                SelfTrainingService.RunIfNeeded();

                if (_skillRegistry == null)
                    _skillRegistry = SkillRegistry.CreateDefault();

                if (_queue == null)
                    _queue = new ChatRequestQueue();

                if (_handler == null)
                    _handler = new RevitChatHandler(_queue, _skillRegistry);

                if (_externalEvent == null)
                    _externalEvent = ExternalEvent.Create(_handler);

                var viewModel = new LocalChatViewModel(
                    _externalEvent, _handler, _queue, _skillRegistry);
                _window = new LocalChatWindow
                {
                    DataContext = viewModel
                };

                var helper = new WindowInteropHelper(_window);
                helper.Owner = uiapp.MainWindowHandle;

                _window.Closed += (sender, args) =>
                {
                    viewModel.Cleanup();
                    _window = null;
                };

                _window.Show();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RevitChatLocal Error",
                    $"Failed to open chat window:\n\n{ex.Message}");
            }
        }

        public static void Run(UIApplication uiapp) => ShowTool(uiapp);
    }
}
