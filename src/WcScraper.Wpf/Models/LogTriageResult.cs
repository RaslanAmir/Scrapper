using System;
using System.Collections.Generic;

namespace WcScraper.Wpf.Models;

public sealed record LogTriageIssue(string Category, string Severity, string Description, string Recommendation);

public sealed record LogTriageResult(DateTimeOffset GeneratedAt, string Overview, IReadOnlyList<LogTriageIssue> Issues);
