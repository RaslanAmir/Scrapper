using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core;
using WcScraper.Core.Telemetry;
using Xunit;

namespace WcScraper.Core.Tests;

public sealed class WordPressDirectoryClientTests
{
    [Fact]
    public async Task GetPluginAsync_RetriesAndLogsOnTooManyRequests()
    {
        using var handler = new SequenceHandler(new[]
        {
            CreateTooManyRequestsResponse(),
            CreatePluginResponse()
        });
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var retryPolicy = new HttpRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5));
        var instrumentation = new TestInstrumentation();
        var client = new WordPressDirectoryClient(
            httpClient,
            retryPolicy,
            loggerFactory: NullLoggerFactory.Instance,
            instrumentation: instrumentation);

        var result = await client.GetPluginAsync("test-plugin");

        Assert.NotNull(result);
        Assert.Equal("test-plugin", result!.Slug);
        Assert.Equal("Test Plugin", result.Title);
        Assert.Equal(2, handler.CallCount);
        Assert.Single(instrumentation.RetryContexts);
        var retry = instrumentation.RetryContexts[0];
        Assert.Equal("WordPressDirectory.plugin_information", retry.OperationName);
        Assert.Equal("plugin", retry.EntityType);
        Assert.Equal(1, retry.RetryAttempt);
        Assert.NotNull(retry.RetryDelay);
        Assert.False(string.IsNullOrWhiteSpace(retry.RetryReason));
    }

    private static HttpResponseMessage CreateTooManyRequestsResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        return response;
    }

    private static HttpResponseMessage CreatePluginResponse()
    {
        const string payload = """
        {
            "slug": "test-plugin",
            "name": "Test Plugin",
            "version": "1.0.0",
            "homepage": "https://example.com",
            "download_link": "https://example.com/download"
        }
        """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(_responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                {
                    _responses.Dequeue().Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestInstrumentation : IScraperInstrumentation
    {
        public List<ScraperOperationContext> RetryContexts { get; } = new();

        public IDisposable BeginScope(ScraperOperationContext context) => NullScope.Instance;

        public void RecordRequestStart(ScraperOperationContext context)
        {
        }

        public void RecordRequestSuccess(ScraperOperationContext context, TimeSpan duration, HttpStatusCode? statusCode = null, int retryCount = 0)
        {
        }

        public void RecordRequestFailure(ScraperOperationContext context, TimeSpan duration, Exception exception, HttpStatusCode? statusCode = null, int retryCount = 0)
        {
        }

        public void RecordRetry(ScraperOperationContext context)
        {
            RetryContexts.Add(context);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
