using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WcScraper.Core;
using Xunit;

namespace WcScraper.Core.Tests;

public class PublicExtensionDetectorTests
{
    [Fact]
    public async Task DetectAsync_FollowsPreloadAndModulePreloadAssetsOnce()
    {
        const string baseUrl = "https://example.com/";
        const string stylesheetUrl = "https://example.com/wp-content/themes/sample-theme/style.css";
        const string scriptUrl = "https://example.com/wp-content/plugins/sample-plugin/module.js";

        var html = $$"""
<html>
    <head>
        <link rel="preload" href="/wp-content/themes/sample-theme/style.css" as="style" />
        <link rel="stylesheet" href="/wp-content/themes/sample-theme/style.css" />
        <link rel="modulepreload" href="/wp-content/plugins/sample-plugin/module.js" />
    </head>
    <body>
        <script src="/wp-content/plugins/sample-plugin/module.js"></script>
    </body>
</html>
""";

        var handler = new RecordingMessageHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [baseUrl] = html,
            [stylesheetUrl] = string.Empty,
            [scriptUrl] = string.Empty
        });

        using var client = new HttpClient(handler, disposeHandler: true);
        using var detector = new PublicExtensionDetector(client);

        var logMessages = new List<string>();
        var progress = new Progress<string>(message => logMessages.Add(message));

        await detector.DetectAsync(baseUrl, followLinkedAssets: true, log: progress);

        Assert.Contains(baseUrl, handler.RequestedUrls);
        Assert.Equal(1, handler.RequestedUrls.Count(url => url == stylesheetUrl));
        Assert.Equal(1, handler.RequestedUrls.Count(url => url == scriptUrl));

        Assert.Contains(logMessages, message =>
            message.Contains("modulepreload", StringComparison.OrdinalIgnoreCase) &&
            message.Contains(scriptUrl, StringComparison.OrdinalIgnoreCase));

        Assert.Contains(logMessages, message =>
            message.Contains("preload stylesheet", StringComparison.OrdinalIgnoreCase) &&
            message.Contains(stylesheetUrl, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;

        public RecordingMessageHandler(Dictionary<string, string> responses)
        {
            _responses = responses;
        }

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);

            if (_responses.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
