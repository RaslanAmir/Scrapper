using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Forms;
using WcScraper.Wpf.Views;

namespace WcScraper.Wpf.Services;

public interface IDialogService
{
    string? BrowseForFolder(string? initial = null);
    void ShowLogWindow(ObservableCollection<string> logs);
}

public sealed class DialogService : IDialogService
{
    private LogWindow? _logWindow;

    public string? BrowseForFolder(string? initial = null)
    {
        using var dlg = new FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(initial))
            dlg.SelectedPath = initial;
        dlg.ShowNewFolderButton = true;
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }

    public void ShowLogWindow(ObservableCollection<string> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        _logWindow ??= CreateLogWindow();
        _logWindow.SetLogs(logs);

        if (!_logWindow.IsVisible)
        {
            if (Application.Current?.MainWindow is not null)
            {
                _logWindow.Owner = Application.Current.MainWindow;
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
