using System;
using System.Collections.Generic;
using WcScraper.Wpf.Services;

namespace WcScraper.Wpf.Models;

public sealed record ExportVerificationIssue(string Severity, string Title, string Description, string? Recommendation)
{
    public bool IsCritical => string.Equals(Severity, "critical", StringComparison.OrdinalIgnoreCase);
    public bool IsWarning => string.Equals(Severity, "warning", StringComparison.OrdinalIgnoreCase);
}

public sealed record ExportVerificationResult(
    DateTimeOffset GeneratedAt,
    string Summary,
    IReadOnlyList<ExportVerificationIssue> Issues,
    IReadOnlyList<string> SuggestedFixes,
    AssistantDirectiveBatch? SuggestedDirectives);
