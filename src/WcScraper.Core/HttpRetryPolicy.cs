using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core.Telemetry;

namespace WcScraper.Core;

public sealed class HttpRetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan? _maxDelay;
    private readonly ISet<HttpStatusCode> _retryableStatusCodes;
    private readonly ILogger<HttpRetryPolicy> _logger;

    private static readonly HttpStatusCode[] DefaultRetryableStatusCodes =
    {
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.GatewayTimeout,
        HttpStatusCode.RequestTimeout,
        (HttpStatusCode)522, // Connection timed out (Cloudflare)
    };

    public HttpRetryPolicy(
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        IEnumerable<HttpStatusCode>? retryableStatusCodes = null,
        ILogger<HttpRetryPolicy>? logger = null,
        ScraperInstrumentationOptions? instrumentationOptions = null)
    {
        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "Retry count cannot be negative.");
        }

        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        if (_baseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), baseDelay, "Base delay must be positive.");
        }

        if (maxDelay is { } delay && delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDelay), maxDelay, "Maximum delay must be positive when specified.");
        }

        _maxDelay = maxDelay;
        _retryableStatusCodes = new HashSet<HttpStatusCode>(retryableStatusCodes ?? DefaultRetryableStatusCodes);
        _logger = logger
            ?? instrumentationOptions?.LoggerFactory?.CreateLogger<HttpRetryPolicy>()
            ?? NullLogger<HttpRetryPolicy>.Instance;
    }

    public Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> sendOperation,
        ScraperOperationContext context,
        IScraperInstrumentation? instrumentation = null,
        CancellationToken cancellationToken = default,
        Action<HttpRetryResult>? onSuccess = null,
        Action<HttpRetryResult>? onFailure = null)
    {
        if (sendOperation is null)
        {
            throw new ArgumentNullException(nameof(sendOperation));
        }

        instrumentation ??= NullScraperInstrumentation.Instance;

        return ExecuteAsync(
            sendOperation,
            context,
            instrumentation,
            cancellationToken,
            onSuccess,
            onFailure);
    }

    private async Task<HttpResponseMessage> ExecuteAsync(
        Func<Task<HttpResponseMessage>> sendOperation,
        ScraperOperationContext context,
        IScraperInstrumentation instrumentation,
        CancellationToken cancellationToken,
        Action<HttpRetryResult>? onSuccess,
        Action<HttpRetryResult>? onFailure)
    {
        using var scope = instrumentation.BeginScope(context);
        instrumentation.RecordRequestStart(context);

        var stopwatch = Stopwatch.StartNew();
        var retryCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                response = await sendOperation().ConfigureAwait(false);

                if (!ShouldRetryResponse(response, retryCount, out var delay, out var reason))
                {
                    stopwatch.Stop();
                    var completedContext = context with { RetryAttempt = retryCount };
                    instrumentation.RecordRequestSuccess(completedContext, stopwatch.Elapsed, response.StatusCode, retryCount);
                    onSuccess?.Invoke(new HttpRetryResult(stopwatch.Elapsed, response.StatusCode, retryCount, null));
                    return response;
                }

                retryCount++;
                var retryContext = context with
                {
                    RetryAttempt = retryCount,
                    RetryDelay = delay,
                    RetryReason = reason
                };

                instrumentation.RecordRetry(retryContext);
                _logger.LogRetryScheduled(delay, retryCount, reason);

                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken))
            {
                response?.Dispose();
                if (retryCount >= _maxRetries)
                {
                    stopwatch.Stop();
                    var statusCode = response?.StatusCode ?? (ex as HttpRequestException)?.StatusCode;
                    var failedContext = context with { RetryAttempt = retryCount };
                    instrumentation.RecordRequestFailure(failedContext, stopwatch.Elapsed, ex, statusCode, retryCount);
                    onFailure?.Invoke(new HttpRetryResult(stopwatch.Elapsed, statusCode, retryCount, ex));
                    throw;
                }

                retryCount++;
                var delay = CalculateBackoffDelay(retryCount);
                var reason = ex.Message;
                var retryContext = context with
                {
                    RetryAttempt = retryCount,
                    RetryDelay = delay,
                    RetryReason = reason
                };

                instrumentation.RecordRetry(retryContext);
                _logger.LogRetryScheduled(delay, retryCount, reason);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response?.Dispose();
                stopwatch.Stop();
                var statusCode = response?.StatusCode ?? (ex as HttpRequestException)?.StatusCode;
                var failedContext = context with { RetryAttempt = retryCount };
                instrumentation.RecordRequestFailure(failedContext, stopwatch.Elapsed, ex, statusCode, retryCount);
                onFailure?.Invoke(new HttpRetryResult(stopwatch.Elapsed, statusCode, retryCount, ex));
                throw;
            }
        }
    }

    private bool ShouldRetryResponse(HttpResponseMessage response, int attempt, out TimeSpan delay, out string reason)
    {
        delay = TimeSpan.Zero;
        reason = string.Empty;

        if (attempt >= _maxRetries)
        {
            return false;
        }

        if (_retryableStatusCodes.Contains(response.StatusCode))
        {
            delay = DetermineRetryDelay(response, attempt + 1);
            var reasonPhrase = string.IsNullOrWhiteSpace(response.ReasonPhrase)
                ? response.StatusCode.ToString()
                : response.ReasonPhrase;
            reason = $"Retryable HTTP {(int)response.StatusCode} {reasonPhrase}".TrimEnd();
            return true;
        }

        return false;
    }

    private TimeSpan DetermineRetryDelay(HttpResponseMessage response, int retryNumber)
    {
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return ApplyMaxDelay(delta);
            }

            if (retryAfter.Date is { } date)
            {
                var dateDelta = date - DateTimeOffset.UtcNow;
                if (dateDelta > TimeSpan.Zero)
                {
                    return ApplyMaxDelay(dateDelta);
                }
            }
        }

        return CalculateBackoffDelay(retryNumber);
    }

    private TimeSpan CalculateBackoffDelay(int retryNumber)
    {
        var multiplier = Math.Pow(2, Math.Max(0, retryNumber - 1));
        var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * multiplier);
        return ApplyMaxDelay(delay);
    }

    private TimeSpan ApplyMaxDelay(TimeSpan delay)
    {
        if (_maxDelay is { } max && delay > max)
        {
            return max;
        }

        return delay;
    }

    private static bool IsTransientException(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is HttpRequestException or IOException or TaskCanceledException;
    }

}

public readonly record struct HttpRetryResult(
    TimeSpan Elapsed,
    HttpStatusCode? StatusCode,
    int RetryCount,
    Exception? Exception);
