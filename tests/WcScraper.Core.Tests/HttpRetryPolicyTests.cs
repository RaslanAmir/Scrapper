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
        var instrumentation = new RecordingInstrumentation();
        var successResults = new List<HttpRetryResult>();
        var failureResults = new List<HttpRetryResult>();
        var context = new ScraperOperationContext("HttpRetryPolicyTests.Transient", "https://example.com/transient");

        using var response = await policy.SendAsync(() =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new HttpRequestException("temporary network error");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            context,
            instrumentation,
            onSuccess: successResults.Add,
            onFailure: failureResults.Add);

        Assert.Equal(2, attempts);
        Assert.Empty(failureResults);

        var success = Assert.Single(successResults);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, success.RetryCount);
        Assert.True(success.Elapsed >= TimeSpan.Zero);
        Assert.Null(success.Exception);

        var instrumentationSuccess = Assert.Single(instrumentation.Successes);
        Assert.Equal(context.OperationName, instrumentationSuccess.Context.OperationName);
        Assert.Equal(HttpStatusCode.OK, instrumentationSuccess.StatusCode);
        Assert.Equal(1, instrumentationSuccess.RetryCount);
        Assert.True(instrumentationSuccess.Duration >= TimeSpan.Zero);
        Assert.Equal(1, instrumentationSuccess.Context.RetryAttempt);

        var scopeContext = Assert.Single(instrumentation.ScopeContexts);
        Assert.Equal(context, scopeContext);

        var startContext = Assert.Single(instrumentation.RequestStarts);
        Assert.Equal(context, startContext);

        var instrumentationRetry = Assert.Single(instrumentation.Retries);
        Assert.Equal(1, instrumentationRetry.RetryAttempt);
        Assert.Equal("temporary network error", instrumentationRetry.RetryReason);
        Assert.Equal(TimeSpan.FromMilliseconds(1), instrumentationRetry.RetryDelay);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_UsesRetryAfterHeader()
    {
        var policy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5));
        var attempts = 0;
        var instrumentation = new RecordingInstrumentation();
        var successResults = new List<HttpRetryResult>();
        var failureResults = new List<HttpRetryResult>();

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
            },
            context,
            instrumentation,
            onSuccess: successResults.Add,
            onFailure: failureResults.Add);

        Assert.Equal(2, attempts);
        Assert.Empty(failureResults);

        var success = Assert.Single(successResults);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, success.RetryCount);

        var instrumentationSuccess = Assert.Single(instrumentation.Successes);
        Assert.Equal(success.StatusCode, instrumentationSuccess.StatusCode);
        Assert.Equal(success.RetryCount, instrumentationSuccess.RetryCount);
        Assert.Equal(1, instrumentationSuccess.Context.RetryAttempt);

        var scopeContext = Assert.Single(instrumentation.ScopeContexts);
        Assert.Equal(context, scopeContext);

        var startContext = Assert.Single(instrumentation.RequestStarts);
        Assert.Equal(context, startContext);

        var instrumentationRetry = Assert.Single(instrumentation.Retries);
        Assert.Equal(1, instrumentationRetry.RetryAttempt);
        Assert.True((instrumentationRetry.RetryDelay ?? TimeSpan.Zero) >= TimeSpan.FromMilliseconds(3));
        Assert.False(string.IsNullOrWhiteSpace(instrumentationRetry.RetryReason));

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
        var instrumentation = new RecordingInstrumentation();
        var successResults = new List<HttpRetryResult>();
        var failureResults = new List<HttpRetryResult>();

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(statusCode));
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        var context = new ScraperOperationContext("HttpRetryPolicyTests.Status", $"https://example.com/{(int)statusCode}");
        using var response = await policy.SendAsync(() =>
            {
                attempts++;
                return Task.FromResult(responses.Dequeue());
            },
            context,
            instrumentation,
            onSuccess: successResults.Add,
            onFailure: failureResults.Add);

        Assert.Equal(2, attempts);
        Assert.Empty(failureResults);

        var success = Assert.Single(successResults);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, success.RetryCount);

        var instrumentationSuccess = Assert.Single(instrumentation.Successes);
        Assert.Equal(success.StatusCode, instrumentationSuccess.StatusCode);
        Assert.Equal(success.RetryCount, instrumentationSuccess.RetryCount);
        Assert.Equal(1, instrumentationSuccess.Context.RetryAttempt);

        var scopeContext = Assert.Single(instrumentation.ScopeContexts);
        Assert.Equal(context, scopeContext);

        var startContext = Assert.Single(instrumentation.RequestStarts);
        Assert.Equal(context, startContext);

        var instrumentationRetry = Assert.Single(instrumentation.Retries);
        Assert.Equal(1, instrumentationRetry.RetryAttempt);
        Assert.Contains(((int)statusCode).ToString(), instrumentationRetry.RetryReason ?? string.Empty);
        Assert.Equal(TimeSpan.FromMilliseconds(1), instrumentationRetry.RetryDelay);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_RespectsCustomRetryableStatuses()
    {
        var policy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5), retryableStatusCodes: new[] { HttpStatusCode.BadRequest });
        var attempts = 0;
        var instrumentation = new RecordingInstrumentation();
        var successResults = new List<HttpRetryResult>();
        var failureResults = new List<HttpRetryResult>();

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

        var context = new ScraperOperationContext("HttpRetryPolicyTests.CustomStatus", "https://example.com/custom");

        using var response = await policy.SendAsync(() =>
            {
                attempts++;
                return Task.FromResult(responses.Dequeue());
            },
            context,
            instrumentation,
            onSuccess: successResults.Add,
            onFailure: failureResults.Add);

        Assert.Equal(2, attempts);
        Assert.Empty(failureResults);

        var success = Assert.Single(successResults);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(1, success.RetryCount);

        var instrumentationSuccess = Assert.Single(instrumentation.Successes);
        Assert.Equal(success.StatusCode, instrumentationSuccess.StatusCode);
        Assert.Equal(success.RetryCount, instrumentationSuccess.RetryCount);
        Assert.Equal(1, instrumentationSuccess.Context.RetryAttempt);

        var scopeContext = Assert.Single(instrumentation.ScopeContexts);
        Assert.Equal(context, scopeContext);

        var startContext = Assert.Single(instrumentation.RequestStarts);
        Assert.Equal(context, startContext);

        var retryContext = Assert.Single(instrumentation.Retries);
        Assert.Equal(TimeSpan.FromMilliseconds(1), retryContext.RetryDelay);
        Assert.Equal(1, retryContext.RetryAttempt);
        Assert.False(string.IsNullOrWhiteSpace(retryContext.RetryReason));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryOnNonRetryableStatus()
    {
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(2));
        var attempts = 0;
        var instrumentation = new RecordingInstrumentation();
        var successResults = new List<HttpRetryResult>();
        var failureResults = new List<HttpRetryResult>();

        var context = new ScraperOperationContext("HttpRetryPolicyTests.NonRetryable", "https://example.com/non-retry");

        using var response = await policy.SendAsync(() =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            },
            context,
            instrumentation,
            onSuccess: successResults.Add,
            onFailure: failureResults.Add);

        Assert.Equal(1, attempts);
        Assert.Empty(instrumentation.Retries);
        Assert.Empty(failureResults);

        var success = Assert.Single(successResults);
        Assert.Equal(HttpStatusCode.NotFound, success.StatusCode);
        Assert.Equal(0, success.RetryCount);

        var instrumentationSuccess = Assert.Single(instrumentation.Successes);
        Assert.Equal(success.StatusCode, instrumentationSuccess.StatusCode);
        Assert.Equal(success.RetryCount, instrumentationSuccess.RetryCount);
        Assert.Equal(0, instrumentationSuccess.Context.RetryAttempt);

        var scopeContext = Assert.Single(instrumentation.ScopeContexts);
        Assert.Equal(context, scopeContext);

        var startContext = Assert.Single(instrumentation.RequestStarts);
        Assert.Equal(context, startContext);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_CancelledBeforeRetryDelay_ThrowsOperationCanceledException()
    {
        using var cancellationSource = new CancellationTokenSource();
        var policy = new HttpRetryPolicy(maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(50), maxDelay: TimeSpan.FromMilliseconds(50));
        var attempts = 0;
        var instrumentation = new RecordingInstrumentation
        {
            OnRecordRetry = _ => cancellationSource.Cancel()
        };
        var successResults = new List<HttpRetryResult>();
        var failureResults = new List<HttpRetryResult>();

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
                instrumentation,
                cancellationToken: cancellationSource.Token,
                onSuccess: successResults.Add,
                onFailure: failureResults.Add));

        Assert.Equal(1, attempts);
        Assert.Empty(successResults);

        var failure = Assert.Single(failureResults);
        Assert.Equal(1, failure.RetryCount);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.Exception);

        var instrumentationFailure = Assert.Single(instrumentation.Failures);
        Assert.Equal(1, instrumentationFailure.RetryCount);
        Assert.IsAssignableFrom<OperationCanceledException>(instrumentationFailure.Exception);
        Assert.Equal(1, instrumentationFailure.Context.RetryAttempt);

        var retryContext = Assert.Single(instrumentation.Retries);
        Assert.Equal(1, retryContext.RetryAttempt);
        Assert.Equal(TimeSpan.FromMilliseconds(50), retryContext.RetryDelay);

        var scopeContext = Assert.Single(instrumentation.ScopeContexts);
        Assert.Equal(context, scopeContext);

        var startContext = Assert.Single(instrumentation.RequestStarts);
        Assert.Equal(context, startContext);
    }

    private sealed class RecordingInstrumentation : IScraperInstrumentation
    {
        private sealed class NoOpScope : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public List<ScraperOperationContext> ScopeContexts { get; } = new();

        public List<ScraperOperationContext> RequestStarts { get; } = new();

        public List<(ScraperOperationContext Context, TimeSpan Duration, HttpStatusCode? StatusCode, int RetryCount)> Successes { get; } = new();

        public List<(ScraperOperationContext Context, TimeSpan Duration, Exception Exception, HttpStatusCode? StatusCode, int RetryCount)> Failures { get; } = new();

        public List<ScraperOperationContext> Retries { get; } = new();

        public Action<ScraperOperationContext>? OnRecordRetry { get; set; }

        public IDisposable BeginScope(ScraperOperationContext context)
        {
            ScopeContexts.Add(context);
            return new NoOpScope();
        }

        public void RecordRequestStart(ScraperOperationContext context)
        {
            RequestStarts.Add(context);
        }

        public void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0)
        {
            Successes.Add((context, duration, statusCode, retryCount));
        }

        public void RecordRequestFailure(ScraperOperationContext context, TimeSpan duration, Exception exception, HttpStatusCode? statusCode = null, int retryCount = 0)
        {
            Failures.Add((context, duration, exception, statusCode, retryCount));
        }

        public void RecordRetry(ScraperOperationContext context)
        {
            Retries.Add(context);
            OnRecordRetry?.Invoke(context);
        }
    }
}
