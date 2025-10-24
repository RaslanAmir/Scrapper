using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.Views;

public partial class ReportCompletionWindow : Window
{
    private readonly ManualRunCompletionInfo _info;

    public ReportCompletionWindow(ManualRunCompletionInfo info)
    {
        InitializeComponent();
        _info = info ?? throw new ArgumentNullException(nameof(info));
        DataContext = _info;
    }

    private void OnOpenReport(object sender, RoutedEventArgs e)
        => TryOpenPath(_info.ReportPath);

    private void OnOpenBundle(object? sender, RoutedEventArgs e)
        => TryOpenPath(_info.ManualBundlePath);

    private void OnOpenAiBrief(object? sender, RoutedEventArgs e)
        => TryOpenPath(_info.AiBriefPath);

    private void OnOpenRunDelta(object? sender, RoutedEventArgs e)
        => TryOpenPath(_info.RunDeltaPath);

    private void OnAskFollowUp(object sender, RoutedEventArgs e)
    {
        try
        {
            _info.AskFollowUp?.Invoke();
        }
        catch
        {
            // Swallow exceptions from callbacks to avoid blocking the UI.
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
        => Close();

    private static void TryOpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                };
                Process.Start(startInfo);
            }
        }
        catch
        {
            // Ignore failures; opening artifacts is best-effort.
        }
    }
}
