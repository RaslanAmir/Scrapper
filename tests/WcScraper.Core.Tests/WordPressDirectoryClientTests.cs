using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
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
        var client = new WordPressDirectoryClient(httpClient, retryPolicy);
        var logMessages = new List<string>();
        var log = new Progress<string>(message => logMessages.Add(message));

        var result = await client.GetPluginAsync("test-plugin", log: log).ConfigureAwait(false);

        Assert.NotNull(result);
        Assert.Equal("test-plugin", result!.Slug);
        Assert.Equal("Test Plugin", result.Title);
        Assert.Equal(2, handler.CallCount);
        Assert.Single(logMessages);
        Assert.Contains("Retrying WordPress.org request for slug 'test-plugin'", logMessages[0]);
    }

    private static HttpResponseMessage CreateTooManyRequestsResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        return response;
    }

    private static HttpResponseMessage CreatePluginResponse()
    {
        const string payload = "{""slug"":""test-plugin"",""name"":""Test Plugin"",""version"":""1.0.0"",""homepage"":""https://example.com"",""download_link"":""https://example.com/download""}";
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
}
