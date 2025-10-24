using System;
using System.Windows;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf.Views
{
    public partial class OnboardingWizardWindow : Window
    {
        public OnboardingWizardWindow()
        {
            InitializeComponent();
            Loaded += OnWindowLoaded;
            DataContextChanged += OnDataContextChanged;
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is OnboardingWizardViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is OnboardingWizardViewModel oldViewModel)
            {
                oldViewModel.Completed -= OnViewModelCompleted;
                oldViewModel.Cancelled -= OnViewModelCancelled;
            }

            if (e.NewValue is OnboardingWizardViewModel newViewModel)
            {
                newViewModel.Completed += OnViewModelCompleted;
                newViewModel.Cancelled += OnViewModelCancelled;
            }
        }

        private void OnViewModelCompleted(object? sender, EventArgs e)
        {
            if (sender is OnboardingWizardViewModel viewModel)
            {
                DialogResult = viewModel.Result is not null;
            }

            Close();
        }

        private void OnViewModelCancelled(object? sender, EventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is OnboardingWizardViewModel viewModel)
            {
                viewModel.Completed -= OnViewModelCompleted;
                viewModel.Cancelled -= OnViewModelCancelled;
                viewModel.Dispose();
            }

            base.OnClosed(e);
        }
    }
}
