using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WcScraper.Core;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.ViewModels;
using Xunit;

namespace WcScraper.Wpf.Tests;

public class ProvisioningOrchestrationServiceTests
{
    [Fact]
    public async Task ExecuteProvisioningAsync_WhenReplicationUnavailable_AppendsGuidance()
    {
        var workflow = new StubProvisioningWorkflow
        {
            CanReplicate = false
        };
        var service = CreateService(workflow);

        var messages = new List<string>();
        var request = CreateRequest(
            append: messages.Add,
            prepareCancellation: () => throw new InvalidOperationException("Cancellation token should not be created."));

        await service.ExecuteProvisioningAsync(request);

        Assert.Single(messages);
        Assert.Equal("Run an export before provisioning.", messages[0]);
        Assert.False(workflow.ExecuteCalled);
        Assert.False(workflow.ResetCalled);
    }

    [Fact]
    public async Task ExecuteProvisioningAsync_WhenCredentialsMissing_AppendsGuidance()
    {
        var workflow = new StubProvisioningWorkflow
        {
            HasTargetCredentials = false
        };
        var service = CreateService(workflow);

        var messages = new List<string>();
        var request = CreateRequest(
            append: messages.Add,
            prepareCancellation: () => throw new InvalidOperationException("Cancellation token should not be created."));

        await service.ExecuteProvisioningAsync(request);

        Assert.Single(messages);
        Assert.Equal("Enter the target store URL, consumer key, and consumer secret before provisioning.", messages[0]);
        Assert.False(workflow.ExecuteCalled);
    }

    [Fact]
    public async Task ExecuteProvisioningAsync_WhenReady_OrchestratesProvisioning()
    {
        var workflow = new StubProvisioningWorkflow();
        var service = CreateService(workflow);
        using var preparedToken = new CancellationTokenSource();
        var runStarted = false;
        var runCompleted = false;

        var request = CreateRequest(
            append: _ => { },
            prepareCancellation: () =>
            {
                return preparedToken.Token;
            },
            onRunStarted: () => runStarted = true,
            onRunCompleted: () => runCompleted = true,
            wordPressUsername: "user",
            wordPressApplicationPassword: "password");

        await service.ExecuteProvisioningAsync(request);

        Assert.True(workflow.ExecuteCalled);
        Assert.Equal("user", workflow.LastUsername);
        Assert.Equal("password", workflow.LastApplicationPassword);
        Assert.Same(request.CreateOperationLogger, workflow.LastCreateLogger);
        Assert.Same(request.RequireProgressLogger, workflow.LastRequireProgressLogger);
        Assert.Same(request.Append, workflow.LastAppend);
        Assert.Equal(preparedToken.Token, workflow.LastCancellationToken);
        Assert.True(runStarted);
        Assert.True(runCompleted);
    }

    [Fact]
    public void UpdateProvisioningContext_ForwardsToWorkflow()
    {
        var workflow = new StubProvisioningWorkflow();
        var service = CreateService(workflow);
        var products = new List<StoreProduct>();
        var variations = new List<StoreProduct>();
        var configuration = new StoreConfiguration();
        var pluginBundles = new List<ExtensionArtifact>();
        var themeBundles = new List<ExtensionArtifact>();
        var customers = new List<WooCustomer>();
        var coupons = new List<WooCoupon>();
        var orders = new List<WooOrder>();
        var subscriptions = new List<WooSubscription>();
        var siteContent = new WordPressSiteContent();
        var categories = new List<TermItem>();

        service.UpdateProvisioningContext(
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
            categories);

        Assert.Same(products, workflow.LastContextProducts);
        Assert.Same(variations, workflow.LastContextVariations);
        Assert.Same(configuration, workflow.LastContextConfiguration);
        Assert.Same(pluginBundles, workflow.LastContextPluginBundles);
        Assert.Same(themeBundles, workflow.LastContextThemeBundles);
        Assert.Same(customers, workflow.LastContextCustomers);
        Assert.Same(coupons, workflow.LastContextCoupons);
        Assert.Same(orders, workflow.LastContextOrders);
        Assert.Same(subscriptions, workflow.LastContextSubscriptions);
        Assert.Same(siteContent, workflow.LastContextSiteContent);
        Assert.Same(categories, workflow.LastContextCategoryTerms);
    }

    [Fact]
    public void ResetProvisioningContext_ForwardsToWorkflow()
    {
        var workflow = new StubProvisioningWorkflow();
        var service = CreateService(workflow);

        service.ResetProvisioningContext();

        Assert.True(workflow.ResetCalled);
    }

    private static ProvisioningOrchestrationService CreateService(IProvisioningWorkflow workflow)
    {
        return new ProvisioningOrchestrationService(workflow, new ProvisioningViewModel());
    }

    private static ProvisioningOrchestrationRequest CreateRequest(
        Action<string>? append = null,
        Func<CancellationToken>? prepareCancellation = null,
        Action? onRunStarted = null,
        Action? onRunCompleted = null,
        string? wordPressUsername = null,
        string? wordPressApplicationPassword = null)
    {
        append ??= _ => { };
        prepareCancellation ??= () => CancellationToken.None;
        onRunStarted ??= () => { };
        onRunCompleted ??= () => { };

        return new ProvisioningOrchestrationRequest(
            wordPressUsername,
            wordPressApplicationPassword,
            (name, url, platform, context, level) => new LoggerProgressAdapter(NullLogger.Instance),
            logger => new LoggerProgressAdapter(logger),
            append,
            prepareCancellation,
            onRunStarted,
            onRunCompleted);
    }

    private sealed class StubProvisioningWorkflow : IProvisioningWorkflow
    {
        public bool CanReplicate { get; set; } = true;

        public bool HasTargetCredentials { get; set; } = true;

        public bool ExecuteCalled { get; private set; }

        public bool ResetCalled { get; private set; }

        public string? LastUsername { get; private set; }

        public string? LastApplicationPassword { get; private set; }

        public Func<string, string?, string?, IReadOnlyDictionary<string, object?>?, LogLevel, LoggerProgressAdapter>? LastCreateLogger { get; private set; }

        public Func<ILogger, IProgress<string>>? LastRequireProgressLogger { get; private set; }

        public Action<string>? LastAppend { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public List<StoreProduct>? LastContextProducts { get; private set; }

        public List<StoreProduct>? LastContextVariations { get; private set; }

        public StoreConfiguration? LastContextConfiguration { get; private set; }

        public List<ExtensionArtifact>? LastContextPluginBundles { get; private set; }

        public List<ExtensionArtifact>? LastContextThemeBundles { get; private set; }

        public List<WooCustomer>? LastContextCustomers { get; private set; }

        public List<WooCoupon>? LastContextCoupons { get; private set; }

        public List<WooOrder>? LastContextOrders { get; private set; }

        public List<WooSubscription>? LastContextSubscriptions { get; private set; }

        public WordPressSiteContent? LastContextSiteContent { get; private set; }

        public List<TermItem>? LastContextCategoryTerms { get; private set; }

        public Task ExecuteProvisioningAsync(
            string? wordPressUsername,
            string? wordPressApplicationPassword,
            Func<string, string?, string?, IReadOnlyDictionary<string, object?>?, LogLevel, LoggerProgressAdapter> createOperationLogger,
            Func<ILogger, IProgress<string>> requireProgressLogger,
            Action<string> append,
            CancellationToken cancellationToken)
        {
            ExecuteCalled = true;
            LastUsername = wordPressUsername;
            LastApplicationPassword = wordPressApplicationPassword;
            LastCreateLogger = createOperationLogger;
            LastRequireProgressLogger = requireProgressLogger;
            LastAppend = append;
            LastCancellationToken = cancellationToken;
            return Task.CompletedTask;
        }

        public void ResetProvisioningContext()
        {
            ResetCalled = true;
        }

        public void SetHostBusy(bool isBusy)
        {
        }

        public void SetProvisioningContext(
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
        {
            LastContextProducts = products;
            LastContextVariations = variations;
            LastContextConfiguration = configuration;
            LastContextPluginBundles = pluginBundles;
            LastContextThemeBundles = themeBundles;
            LastContextCustomers = customers;
            LastContextCoupons = coupons;
            LastContextOrders = orders;
            LastContextSubscriptions = subscriptions;
            LastContextSiteContent = siteContent;
            LastContextCategoryTerms = categoryTerms;
        }
    }
}
