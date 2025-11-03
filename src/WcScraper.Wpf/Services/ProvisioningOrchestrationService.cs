using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WcScraper.Core;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf.Services;

public interface IProvisioningOrchestrationService
{
    ProvisioningViewModel ProvisioningViewModel { get; }

    Task ExecuteProvisioningAsync(ProvisioningOrchestrationRequest request);

    void ResetProvisioningContext();

    void UpdateProvisioningContext(
        List<StoreProduct> products,
        List<StoreProduct> variations,
        StoreConfiguration? configuration,
        List<ExtensionArtifact> pluginBundles,
        List<ExtensionArtifact> themeBundles,
        List<WooCustomer> customers,
        List<WooCoupon> coupons,
        List<WooOrder> orders,
        List<WooSubscription> subscriptions,
        WordPressSiteContent? siteContent,
        List<TermItem> categoryTerms);
}

public sealed class ProvisioningOrchestrationService : IProvisioningOrchestrationService
{
    private readonly IProvisioningWorkflow _workflow;

    public ProvisioningOrchestrationService()
        : this(new ProvisioningViewModel())
    {
    }

    public ProvisioningOrchestrationService(ProvisioningViewModel provisioningViewModel)
        : this(provisioningViewModel, provisioningViewModel)
    {
    }

    internal ProvisioningOrchestrationService(IProvisioningWorkflow workflow, ProvisioningViewModel provisioningViewModel)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        ProvisioningViewModel = provisioningViewModel ?? throw new ArgumentNullException(nameof(provisioningViewModel));
    }

    public ProvisioningViewModel ProvisioningViewModel { get; }

    public void ResetProvisioningContext() => _workflow.ResetProvisioningContext();

    public void UpdateProvisioningContext(
        List<StoreProduct> products,
        List<StoreProduct> variations,
        StoreConfiguration? configuration,
        List<ExtensionArtifact> pluginBundles,
        List<ExtensionArtifact> themeBundles,
        List<WooCustomer> customers,
        List<WooCoupon> coupons,
        List<WooOrder> orders,
        List<WooSubscription> subscriptions,
        WordPressSiteContent? siteContent,
        List<TermItem> categoryTerms)
        => _workflow.SetProvisioningContext(
            products,
            variations,
            configuration,
            pluginBundles,
            themeBundles,
            customers,
            coupons,
            orders,
            subscriptions,
            siteContent,
            categoryTerms);

    public async Task ExecuteProvisioningAsync(ProvisioningOrchestrationRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!_workflow.CanReplicate)
        {
            request.Append("Run an export before provisioning.");
            return;
        }

        if (!_workflow.HasTargetCredentials)
        {
            request.Append("Enter the target store URL, consumer key, and consumer secret before provisioning.");
            return;
        }

        var cancellationToken = request.PrepareRunCancellationToken();

        try
        {
            request.OnRunStarting();

            await _workflow.ExecuteProvisioningAsync(
                request.WordPressUsername,
                request.WordPressApplicationPassword,
                request.CreateOperationLogger,
                request.RequireProgressLogger,
                request.Append,
                cancellationToken);
        }
        finally
        {
            request.OnRunCompleted();
        }
    }
}

public sealed class ProvisioningOrchestrationRequest
{
    public ProvisioningOrchestrationRequest(
        string? wordPressUsername,
        string? wordPressApplicationPassword,
        Func<string, string?, string?, IReadOnlyDictionary<string, object?>?, LogLevel, LoggerProgressAdapter> createOperationLogger,
        Func<ILogger, IProgress<string>> requireProgressLogger,
        Action<string> append,
        Func<CancellationToken> prepareRunCancellationToken,
        Action onRunStarting,
        Action onRunCompleted)
    {
        WordPressUsername = wordPressUsername;
        WordPressApplicationPassword = wordPressApplicationPassword;
        CreateOperationLogger = createOperationLogger ?? throw new ArgumentNullException(nameof(createOperationLogger));
        RequireProgressLogger = requireProgressLogger ?? throw new ArgumentNullException(nameof(requireProgressLogger));
        Append = append ?? throw new ArgumentNullException(nameof(append));
        PrepareRunCancellationToken = prepareRunCancellationToken ?? throw new ArgumentNullException(nameof(prepareRunCancellationToken));
        OnRunStarting = onRunStarting ?? throw new ArgumentNullException(nameof(onRunStarting));
        OnRunCompleted = onRunCompleted ?? throw new ArgumentNullException(nameof(onRunCompleted));
    }

    public string? WordPressUsername { get; }

    public string? WordPressApplicationPassword { get; }

    public Func<string, string?, string?, IReadOnlyDictionary<string, object?>?, LogLevel, LoggerProgressAdapter> CreateOperationLogger { get; }

    public Func<ILogger, IProgress<string>> RequireProgressLogger { get; }

    public Action<string> Append { get; }

    public Func<CancellationToken> PrepareRunCancellationToken { get; }

    public Action OnRunStarting { get; }

    public Action OnRunCompleted { get; }
}
