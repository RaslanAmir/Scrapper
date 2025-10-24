using System;
using System.Windows;
using Forms = System.Windows.Forms;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.ViewModels;
using WcScraper.Wpf.Views;

namespace WcScraper.Wpf.Services;

public interface IDialogService
{
    string? BrowseForFolder(string? initial = null);
    void ShowLogWindow(WcScraper.Wpf.ViewModels.MainViewModel viewModel);
    void ShowRunCompletionDialog(ManualRunCompletionInfo info);
    OnboardingWizardSettings? ShowOnboardingWizard(MainViewModel viewModel, ChatAssistantService chatAssistantService);
    string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null);
}

public sealed class DialogService : IDialogService
{
    private LogWindow? _logWindow;

    public string? BrowseForFolder(string? initial = null)
    {
        using var dlg = new Forms.FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(initial))
            dlg.SelectedPath = initial;
        dlg.ShowNewFolderButton = true;
        return dlg.ShowDialog() == Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    public void ShowLogWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _logWindow ??= CreateLogWindow();
        _logWindow.SetLogs(viewModel);

        if (!_logWindow.IsVisible)
        {
            if (System.Windows.Application.Current?.MainWindow is not null)
            {
                _logWindow.Owner = System.Windows.Application.Current.MainWindow;
            }

            _logWindow.Show();
        }
        else
        {
            _logWindow.Activate();
        }
    }

    private static LogWindow CreateLogWindow()
    {
        var window = new LogWindow();
        return window;
    }

    public void ShowRunCompletionDialog(ManualRunCompletionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var window = new ReportCompletionWindow(info);
        if (System.Windows.Application.Current?.MainWindow is not null && window.Owner is null)
        {
            window.Owner = System.Windows.Application.Current.MainWindow;
        }

        window.ShowDialog();
    }

    public OnboardingWizardSettings? ShowOnboardingWizard(MainViewModel viewModel, ChatAssistantService chatAssistantService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(chatAssistantService);

        var wizardViewModel = new OnboardingWizardViewModel(viewModel, chatAssistantService);
        var window = new OnboardingWizardWindow
        {
            DataContext = wizardViewModel
        };

        if (System.Windows.Application.Current?.MainWindow is not null && window.Owner is null)
        {
            window.Owner = System.Windows.Application.Current.MainWindow;
        }

        var dialogResult = window.ShowDialog();
        return dialogResult == true ? wizardViewModel.Result : null;
    }

    public string? SaveFile(string filter, string defaultFileName, string? initialDirectory = null)
    {
        using var dlg = new Forms.SaveFileDialog
        {
            Filter = string.IsNullOrWhiteSpace(filter) ? "All files (*.*)|*.*" : filter,
            FileName = defaultFileName,
            AddExtension = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            dlg.InitialDirectory = initialDirectory;
        }

        return dlg.ShowDialog() == Forms.DialogResult.OK ? dlg.FileName : null;
    }
}
