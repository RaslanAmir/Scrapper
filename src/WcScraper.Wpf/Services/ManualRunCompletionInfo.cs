using System;
using System.Collections.Generic;

namespace WcScraper.Wpf.Services;

public sealed class ManualRunCompletionInfo
{
    public string StoreIdentifier { get; init; } = string.Empty;
    public string? StoreUrl { get; init; }
    public string ReportPath { get; init; } = string.Empty;
    public string? ManualBundlePath { get; init; }
    public string? AiBriefPath { get; init; }
    public string? RunDeltaPath { get; init; }
    public string? ExportVerificationPath { get; init; }
    public string? ExportVerificationSummary { get; init; }
    public IReadOnlyList<string> ExportVerificationAlerts { get; init; } = Array.Empty<string>();
    public bool HasCriticalExportFindings { get; init; }
    public Action? AskFollowUp { get; init; }

    public bool HasManualBundle => !string.IsNullOrWhiteSpace(ManualBundlePath);
    public bool HasAiBrief => !string.IsNullOrWhiteSpace(AiBriefPath);
    public bool HasRunDelta => !string.IsNullOrWhiteSpace(RunDeltaPath);
    public bool HasExportVerification => !string.IsNullOrWhiteSpace(ExportVerificationPath);
    public bool HasExportVerificationAlerts => ExportVerificationAlerts.Count > 0;
    public bool CanAskFollowUp => AskFollowUp is not null;
}
