using System;
using System.ComponentModel;
using System.Text;
using System.Windows;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf.Views;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel { get; set; }

    private System.Collections.ObjectModel.ObservableCollection<string>? Logs => ViewModel?.Logs;

    public void SetLogs(MainViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
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

        System.Windows.Clipboard.SetText(builder.ToString());
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
