using System;

namespace WcScraper.Wpf.Services;

public sealed class ManualRunCompletionInfo
{
    public string StoreIdentifier { get; init; } = string.Empty;
    public string? StoreUrl { get; init; }
    public string ReportPath { get; init; } = string.Empty;
    public string? ManualBundlePath { get; init; }
    public string? AiBriefPath { get; init; }
    public string? RunDeltaPath { get; init; }
    public Action? AskFollowUp { get; init; }

    public bool HasManualBundle => !string.IsNullOrWhiteSpace(ManualBundlePath);
    public bool HasAiBrief => !string.IsNullOrWhiteSpace(AiBriefPath);
    public bool HasRunDelta => !string.IsNullOrWhiteSpace(RunDeltaPath);
    public bool CanAskFollowUp => AskFollowUp is not null;
}
