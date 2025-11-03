using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WcScraper.Core;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf.Services;

internal interface IProvisioningWorkflow
{
    bool CanReplicate { get; }

    bool HasTargetCredentials { get; }

    void ResetProvisioningContext();

    void SetProvisioningContext(
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

    void SetHostBusy(bool isBusy);

    Task ExecuteProvisioningAsync(
        string? wordPressUsername,
        string? wordPressApplicationPassword,
        Func<string, string?, string?, IReadOnlyDictionary<string, object?>?, LogLevel, LoggerProgressAdapter> createOperationLogger,
        Func<ILogger, IProgress<string>> requireProgressLogger,
        Action<string> append,
        CancellationToken cancellationToken);
}
