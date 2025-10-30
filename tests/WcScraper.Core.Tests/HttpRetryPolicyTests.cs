using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using WcScraper.Core.Telemetry;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class HttpRetryPolicyTests
{
    [Fact]
    public async Task SendAsync_RetriesOnTransientException()
    {
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(2));
        var attempts = 0;
        var notifications = new List<ScraperOperationContext>();
        var context = new ScraperOperationContext("HttpRetryPolicyTests.Transient", "https://example.com/transient");

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("temporary network error");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, context, onRetry: notifications.Add);

        Assert.Equal(2, attempts);
        Assert.Single(notifications);
        Assert.Equal(1, notifications[0].RetryAttempt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_UsesRetryAfterHeader()
    {
        var policy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5));
        var attempts = 0;
        var delays = new List<TimeSpan?>();

        var responses = new Queue<HttpResponseMessage>();
        var retryResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(3));
        responses.Enqueue(retryResponse);
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        var context = new ScraperOperationContext("HttpRetryPolicyTests.RetryAfter", "https://example.com/retry-after");

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(responses.Dequeue());
        }, context, onRetry: retryContext => delays.Add(retryContext.RetryDelay));

        Assert.Equal(2, attempts);
        Assert.Single(delays);
        Assert.True((delays[0] ?? TimeSpan.Zero) >= TimeSpan.FromMilliseconds(3));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)522)]
    public async Task SendAsync_RetriesOnRetryableStatuses(HttpStatusCode statusCode)
    {
        var policy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5));
        var attempts = 0;
        var notifications = new List<ScraperOperationContext>();

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(statusCode));
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        var context = new ScraperOperationContext("HttpRetryPolicyTests.Status", $"https://example.com/{(int)statusCode}");
        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(responses.Dequeue());
        }, context, onRetry: notifications.Add);

        Assert.Equal(2, attempts);
        Assert.Single(notifications);
        Assert.Contains(((int)statusCode).ToString(), notifications[0].RetryReason ?? string.Empty);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_RespectsCustomRetryableStatuses()
    {
        var policy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5), retryableStatusCodes: new[] { HttpStatusCode.BadRequest });
        var attempts = 0;

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        var context = new ScraperOperationContext("HttpRetryPolicyTests.CustomStatus", "https://example.com/custom");

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(responses.Dequeue());
        }, context, NullScraperInstrumentation.Instance);

        Assert.Equal(2, attempts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryOnNonRetryableStatus()
    {
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(2));
        var attempts = 0;

        var context = new ScraperOperationContext("HttpRetryPolicyTests.NonRetryable", "https://example.com/non-retry");

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }, context, NullScraperInstrumentation.Instance);

        Assert.Equal(1, attempts);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_CancelledBeforeRetryDelay_ThrowsOperationCanceledException()
    {
        using var cancellationSource = new CancellationTokenSource();
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(50), maxDelay: TimeSpan.FromMilliseconds(50));
        var attempts = 0;
        var notifications = 0;

        var context = new ScraperOperationContext("HttpRetryPolicyTests.Cancellation", "https://example.com/cancel");
        async Task<HttpResponseMessage> SendOperation()
        {
            attempts++;
            throw new HttpRequestException("temporary failure");
        }

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.SendAsync(
                SendOperation,
                context,
                cancellationToken: cancellationSource.Token,
                onRetry: _ =>
                {
                    notifications++;
                    cancellationSource.Cancel();
                }));

        Assert.Equal(1, attempts);
        Assert.Equal(1, notifications);
    }
}
