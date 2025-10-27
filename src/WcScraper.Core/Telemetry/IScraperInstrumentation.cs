using System;
using System.Net;

namespace WcScraper.Core.Telemetry;

public interface IScraperInstrumentation
{
    IDisposable BeginScope(ScraperOperationContext context);

    void RecordRequestStart(ScraperOperationContext context);

    void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0);

    void RecordRequestFailure(ScraperOperationContext context, TimeSpan duration, Exception exception, HttpStatusCode? statusCode = null, int retryCount = 0);

    void RecordRetry(ScraperOperationContext context);
}
