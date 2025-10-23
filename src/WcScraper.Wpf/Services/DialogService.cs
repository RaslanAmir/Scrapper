using System;
using System.Windows;
using Forms = System.Windows.Forms;
using WcScraper.Wpf.ViewModels;
using WcScraper.Wpf.Views;

namespace WcScraper.Wpf.Services;

public interface IDialogService
{
    string? BrowseForFolder(string? initial = null);
    void ShowLogWindow(WcScraper.Wpf.ViewModels.MainViewModel viewModel);
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
}
