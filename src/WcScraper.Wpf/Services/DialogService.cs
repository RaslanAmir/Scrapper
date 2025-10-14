using System.Windows.Forms;

namespace WcScraper.Wpf.Services;

public interface IDialogService
{
    string? BrowseForFolder(string? initial = null);
}

public sealed class DialogService : IDialogService
{
    public string? BrowseForFolder(string? initial = null)
    {
        using var dlg = new FolderBrowserDialog();
        if (!string.IsNullOrWhiteSpace(initial))
            dlg.SelectedPath = initial;
        dlg.ShowNewFolderButton = true;
        return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
    }
}
