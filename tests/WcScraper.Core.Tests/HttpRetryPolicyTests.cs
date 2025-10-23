using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using WcScraper.Core;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class HttpRetryPolicyTests
{
    [Fact]
    public async Task SendAsync_RetriesOnTransientException()
    {
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(2));
        var attempts = 0;
        var notifications = new List<HttpRetryAttempt>();

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("temporary network error");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, onRetry: notifications.Add);

        Assert.Equal(2, attempts);
        Assert.Single(notifications);
        Assert.Equal(1, notifications[0].AttemptNumber);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_UsesRetryAfterHeader()
    {
        var policy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5));
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var responses = new Queue<HttpResponseMessage>();
        var retryResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        retryResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(3));
        responses.Enqueue(retryResponse);
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(responses.Dequeue());
        }, onRetry: attempt => delays.Add(attempt.Delay));

        Assert.Equal(2, attempts);
        Assert.Single(delays);
        Assert.True(delays[0] >= TimeSpan.FromMilliseconds(3));
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
        var notifications = new List<HttpRetryAttempt>();

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(statusCode));
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(responses.Dequeue());
        }, onRetry: notifications.Add);

        Assert.Equal(2, attempts);
        Assert.Single(notifications);
        Assert.Contains(((int)statusCode).ToString(), notifications[0].Reason);
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

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(responses.Dequeue());
        });

        Assert.Equal(2, attempts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryOnNonRetryableStatus()
    {
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(2));
        var attempts = 0;

        using var response = await policy.SendAsync(() =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        Assert.Equal(1, attempts);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
