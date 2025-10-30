using System;
using System.Net;

namespace WcScraper.Core.Telemetry;

public sealed class DelegatingScraperInstrumentation : IScraperInstrumentation
{
    private readonly IScraperInstrumentation _inner;
    private readonly Func<ScraperOperationContext, IDisposable>? _beginScope;
    private readonly Action<ScraperOperationContext>? _requestStart;
    private readonly Action<ScraperOperationContext, TimeSpan, HttpStatusCode?, int>? _requestSuccess;
    private readonly Action<ScraperOperationContext, TimeSpan, Exception, HttpStatusCode?, int>? _requestFailure;
    private readonly Action<ScraperOperationContext>? _retry;

    public DelegatingScraperInstrumentation(
        IScraperInstrumentation? inner,
        Func<ScraperOperationContext, IDisposable>? beginScope = null,
        Action<ScraperOperationContext>? requestStart = null,
        Action<ScraperOperationContext, TimeSpan, HttpStatusCode?, int>? requestSuccess = null,
        Action<ScraperOperationContext, TimeSpan, Exception, HttpStatusCode?, int>? requestFailure = null,
        Action<ScraperOperationContext>? retry = null)
    {
        _inner = inner ?? NullScraperInstrumentation.Instance;
        _beginScope = beginScope;
        _requestStart = requestStart;
        _requestSuccess = requestSuccess;
        _requestFailure = requestFailure;
        _retry = retry;
    }

    public IDisposable BeginScope(ScraperOperationContext context)
    {
        var innerScope = _inner.BeginScope(context);
        if (_beginScope is null)
        {
            return innerScope;
        }

        var additional = _beginScope(context);
        if (additional is null)
        {
            return innerScope;
        }

        return new CompositeScope(innerScope, additional);
    }

    public void RecordRequestStart(ScraperOperationContext context)
    {
        _inner.RecordRequestStart(context);
        _requestStart?.Invoke(context);
    }

    public void RecordRequestSuccess(
        ScraperOperationContext context,
        TimeSpan duration,
        HttpStatusCode? statusCode = null,
        int retryCount = 0)
    {
        _inner.RecordRequestSuccess(context, duration, statusCode, retryCount);
        _requestSuccess?.Invoke(context, duration, statusCode, retryCount);
    }

    public void RecordRequestFailure(
        ScraperOperationContext context,
        TimeSpan duration,
        Exception exception,
        HttpStatusCode? statusCode = null,
        int retryCount = 0)
    {
        _inner.RecordRequestFailure(context, duration, exception, statusCode, retryCount);
        _requestFailure?.Invoke(context, duration, exception, statusCode, retryCount);
    }

    public void RecordRetry(ScraperOperationContext context)
    {
        _inner.RecordRetry(context);
        _retry?.Invoke(context);
    }

    private sealed class CompositeScope : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly IDisposable _additional;

        public CompositeScope(IDisposable inner, IDisposable additional)
        {
            _inner = inner;
            _additional = additional;
        }

        public void Dispose()
        {
            try
            {
                _additional.Dispose();
            }
            finally
            {
                _inner.Dispose();
            }
        }
    }

}
