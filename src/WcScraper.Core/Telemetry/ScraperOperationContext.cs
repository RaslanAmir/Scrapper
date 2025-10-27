using System;

namespace WcScraper.Core.Telemetry;

public readonly record struct ScraperOperationContext(
    string OperationName,
    string? Url = null,
    string? EntityType = null,
    long? PayloadBytes = null,
    int? RetryAttempt = null,
    TimeSpan? RetryDelay = null,
    string? RetryReason = null);
