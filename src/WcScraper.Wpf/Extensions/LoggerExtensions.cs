using Microsoft.Extensions.Logging;

namespace WcScraper.Wpf.Extensions;

internal static class LoggerExtensions
{
    private static readonly EventId UiLogEvent = new(5000, "UiLog");

    public static void Report(this ILogger logger, string message)
    {
        logger.Report(LogLevel.Information, message);
    }

    public static void Report(this ILogger logger, LogLevel level, string message)
    {
        if (logger is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        logger.Log(level, UiLogEvent, message, null, static (state, _) => state ?? string.Empty);
    }
}
