using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitChatLocal.ViewModel;

namespace RevitChatLocal.UI
{
    public partial class LocalChatWindow : Window
    {
        public LocalChatWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InputBox.Focus();

            if (DataContext is LocalChatViewModel vm)
            {
                ((INotifyCollectionChanged)vm.Messages).CollectionChanged += OnMessagesChanged;
            }
        }

        private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (ChatList.Items.Count > 0)
            {
                ChatList.ScrollIntoView(ChatList.Items[ChatList.Items.Count - 1]);
            }
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (DataContext is LocalChatViewModel vm && vm.SendCommand.CanExecute(null))
                {
                    vm.SendCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }
}
