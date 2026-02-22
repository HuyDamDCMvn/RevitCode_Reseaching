using System;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using RevitChat.Handler;
using RevitChat.Services;
using RevitChat.Skills;
using RevitChat.UI;
using RevitChat.ViewModel;

namespace RevitChat
{
    public static class Entry
    {
        private static RevitChatWindow _window;
        private static ExternalEvent _externalEvent;
        private static RevitChatHandler _handler;
        private static ChatRequestQueue _queue;
        private static SkillRegistry _skillRegistry;

        public static void ShowTool(UIApplication uiapp)
        {
            if (uiapp == null)
            {
                TaskDialog.Show("RevitChat Error", "UIApplication is null. Cannot open chat window.");
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
                    TaskDialog.Show("RevitChat", "Please open a Revit project first.");
                    return;
                }

                var dllDir = System.IO.Path.GetDirectoryName(typeof(Entry).Assembly.Location)
                            ?? AppContext.BaseDirectory;
                ChatFeedbackService.Initialize(dllDir);

                if (_skillRegistry == null)
                    _skillRegistry = SkillRegistry.CreateDefault();

                if (_queue == null)
                    _queue = new ChatRequestQueue();

                if (_handler == null)
                    _handler = new RevitChatHandler(_queue, _skillRegistry);

                if (_externalEvent == null)
                    _externalEvent = ExternalEvent.Create(_handler);

                var viewModel = new RevitChatViewModel(
                    _externalEvent, _handler, _queue, _skillRegistry);
                _window = new RevitChatWindow
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
                TaskDialog.Show("RevitChat Error",
                    $"Failed to open chat window:\n\n{ex.Message}");
            }
        }

        public static void Run(UIApplication uiapp) => ShowTool(uiapp);
    }
}
