using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;

namespace WcScraper.Wpf.Views;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public ObservableCollection<string>? Logs
    {
        get => DataContext as ObservableCollection<string>;
        private set => DataContext = value;
    }

    public void SetLogs(ObservableCollection<string> logs)
    {
        Logs = logs ?? throw new ArgumentNullException(nameof(logs));
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (Logs is null || Logs.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var entry in Logs)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(entry);
        }

        Clipboard.SetText(builder.ToString());
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Logs?.Clear();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
