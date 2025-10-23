using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WcScraper.Core;

public sealed class HttpRetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan? _maxDelay;

    public HttpRetryPolicy(int maxRetries = 3, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
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
    }

    public Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> sendOperation,
        CancellationToken cancellationToken = default,
        Action<HttpRetryAttempt>? onRetry = null)
    {
        if (sendOperation is null)
        {
            throw new ArgumentNullException(nameof(sendOperation));
        }

        return ExecuteAsync(sendOperation, cancellationToken, onRetry);
    }

    private async Task<HttpResponseMessage> ExecuteAsync(
        Func<Task<HttpResponseMessage>> sendOperation,
        CancellationToken cancellationToken,
        Action<HttpRetryAttempt>? onRetry)
    {
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                response = await sendOperation().ConfigureAwait(false);

                if (!ShouldRetryResponse(response, attempt, out var delay, out var reason))
                {
                    return response;
                }

                attempt++;
                response.Dispose();
                NotifyRetry(onRetry, attempt, delay, reason);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientException(ex, cancellationToken))
            {
                response?.Dispose();
                if (attempt >= _maxRetries)
                {
                    throw;
                }

                attempt++;
                var delay = CalculateBackoffDelay(attempt);
                var reason = ex.Message;
                NotifyRetry(onRetry, attempt, delay, reason);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            delay = DetermineRetryDelay(response, attempt + 1);
            reason = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
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

    private static void NotifyRetry(Action<HttpRetryAttempt>? onRetry, int attempt, TimeSpan delay, string reason)
    {
        onRetry?.Invoke(new HttpRetryAttempt(attempt, delay, reason));
    }
}

public readonly record struct HttpRetryAttempt(int AttemptNumber, TimeSpan Delay, string Reason);
