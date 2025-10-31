using System;
using Microsoft.Extensions.Logging;

namespace WcScraper.Core.Telemetry;

public static class HttpRetryPolicyLoggerExtensions
{
    private static readonly Action<ILogger<global::WcScraper.Core.HttpRetryPolicy>, double, int, string?, Exception?> LogRetryScheduledDelegate =
        LoggerMessage.Define<double, int, string?>
        (
            LogLevel.Warning,
            ScraperTelemetry.Events.RetryScheduled,
            "Retry scheduled in {DelayMilliseconds}ms (attempt {RetryAttempt}): {Reason}"
        );

    public static void LogRetryScheduled(this ILogger<global::WcScraper.Core.HttpRetryPolicy> logger, TimeSpan delay, int retryAttempt, string? reason)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;

        LogRetryScheduledDelegate(logger, delay.TotalMilliseconds, retryAttempt, normalizedReason, null);
    }
}
