using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf
{
    public partial class MainWindow : Window
    {
        private bool _suppressPasswordUpdate;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnMainWindowDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is MainViewModel oldViewModel)
            {
                oldViewModel.ChatAssistant.PropertyChanged -= OnChatAssistantPropertyChanged;
            }

            if (e.NewValue is MainViewModel newViewModel)
            {
                newViewModel.ChatAssistant.PropertyChanged += OnChatAssistantPropertyChanged;
                if (ChatApiKeyBox is not null)
                {
                    _suppressPasswordUpdate = true;
                    ChatApiKeyBox.Password = newViewModel.ChatAssistant.ChatApiKey ?? string.Empty;
                    _suppressPasswordUpdate = false;
                }
            }
        }

        private void OnChatAssistantPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(ChatAssistantViewModel.ChatApiKey), StringComparison.Ordinal))
            {
                return;
            }

            if (sender is ChatAssistantViewModel chatAssistant && ChatApiKeyBox is not null)
            {
                _suppressPasswordUpdate = true;
                ChatApiKeyBox.Password = chatAssistant.ChatApiKey ?? string.Empty;
                _suppressPasswordUpdate = false;
            }
        }

        private void OnChatApiKeyChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressPasswordUpdate)
            {
                return;
            }

            if (DataContext is MainViewModel viewModel && sender is PasswordBox passwordBox)
            {
                viewModel.ChatAssistant.ChatApiKey = passwordBox.Password;
            }
        }
    }
}
