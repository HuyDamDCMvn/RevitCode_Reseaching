using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitChat.ViewModel;

namespace RevitChat.UI
{
    public partial class RevitChatWindow : Window
    {
        private INotifyCollectionChanged _messagesCollection;

        public RevitChatWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InputBox.Focus();

            if (DataContext is RevitChatViewModel vm)
            {
                _messagesCollection = (INotifyCollectionChanged)vm.Messages;
                _messagesCollection.CollectionChanged += OnMessagesChanged;

                if (!string.IsNullOrEmpty(vm.ApiKey))
                    ApiKeyBox.Password = vm.ApiKey;
            }
        }

        private void OnClosed(object sender, System.EventArgs e)
        {
            if (_messagesCollection != null)
                _messagesCollection.CollectionChanged -= OnMessagesChanged;
        }

        private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (ChatList.Items.Count > 0)
                ChatList.ScrollIntoView(ChatList.Items[ChatList.Items.Count - 1]);
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (DataContext is RevitChatViewModel vm && vm.SendCommand.CanExecute(null))
                {
                    vm.SendCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is RevitChatViewModel vm)
                vm.ApiKey = ApiKeyBox.Password;
        }
    }
}
