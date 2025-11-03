using System;
using System.Collections.Generic;
using System.ComponentModel;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf.Services;

public sealed class ChatAssistantWorkflowService
{
    private static readonly HashSet<string> s_chatPreferencePropertyNames = new(StringComparer.Ordinal)
    {
        nameof(ChatAssistantViewModel.ChatApiEndpoint),
        nameof(ChatAssistantViewModel.ChatModel),
        nameof(ChatAssistantViewModel.ChatSystemPrompt),
        nameof(ChatAssistantViewModel.ChatMaxPromptTokens),
        nameof(ChatAssistantViewModel.ChatMaxTotalTokens),
        nameof(ChatAssistantViewModel.ChatMaxCostUsd),
        nameof(ChatAssistantViewModel.ChatPromptTokenUsdPerThousand),
        nameof(ChatAssistantViewModel.ChatCompletionTokenUsdPerThousand),
    };

    public Dictionary<string, (Func<bool> Getter, Action<bool> Setter)> CreateChatAssistantToggleBindings(
        Func<MainViewModel> hostProvider)
    {
        if (hostProvider is null)
        {
            throw new ArgumentNullException(nameof(hostProvider));
        }

        return new Dictionary<string, (Func<bool> Getter, Action<bool> Setter)>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(MainViewModel.ExportCsv)] = (() => hostProvider().ExportCsv, value => hostProvider().ExportCsv = value),
            [nameof(MainViewModel.ExportShopify)] = (() => hostProvider().ExportShopify, value => hostProvider().ExportShopify = value),
            [nameof(MainViewModel.ExportWoo)] = (() => hostProvider().ExportWoo, value => hostProvider().ExportWoo = value),
            [nameof(MainViewModel.ExportReviews)] = (() => hostProvider().ExportReviews, value => hostProvider().ExportReviews = value),
            [nameof(MainViewModel.ExportXlsx)] = (() => hostProvider().ExportXlsx, value => hostProvider().ExportXlsx = value),
            [nameof(MainViewModel.ExportJsonl)] = (() => hostProvider().ExportJsonl, value => hostProvider().ExportJsonl = value),
            [nameof(MainViewModel.ExportPluginsCsv)] = (() => hostProvider().ExportPluginsCsv, value => hostProvider().ExportPluginsCsv = value),
            [nameof(MainViewModel.ExportPluginsJsonl)] = (() => hostProvider().ExportPluginsJsonl, value => hostProvider().ExportPluginsJsonl = value),
            [nameof(MainViewModel.ExportThemesCsv)] = (() => hostProvider().ExportThemesCsv, value => hostProvider().ExportThemesCsv = value),
            [nameof(MainViewModel.ExportThemesJsonl)] = (() => hostProvider().ExportThemesJsonl, value => hostProvider().ExportThemesJsonl = value),
            [nameof(MainViewModel.ExportPublicExtensionFootprints)] = (() => hostProvider().ExportPublicExtensionFootprints, value => hostProvider().ExportPublicExtensionFootprints = value),
            [nameof(MainViewModel.ExportPublicDesignSnapshot)] = (() => hostProvider().ExportPublicDesignSnapshot, value => hostProvider().ExportPublicDesignSnapshot = value),
            [nameof(MainViewModel.ExportPublicDesignScreenshots)] = (() => hostProvider().ExportPublicDesignScreenshots, value => hostProvider().ExportPublicDesignScreenshots = value),
            [nameof(MainViewModel.ExportStoreConfiguration)] = (() => hostProvider().ExportStoreConfiguration, value => hostProvider().ExportStoreConfiguration = value),
            [nameof(ProvisioningViewModel.ImportStoreConfiguration)] = (() => hostProvider().Provisioning.ImportStoreConfiguration, value => hostProvider().Provisioning.ImportStoreConfiguration = value),
            [nameof(MainViewModel.EnableHttpRetries)] = (() => hostProvider().EnableHttpRetries, value => hostProvider().EnableHttpRetries = value),
        };
    }

    public void ConfigureChatAssistant(
        ChatAssistantViewModel chatAssistant,
        IReadOnlyDictionary<string, (Func<bool> Getter, Action<bool> Setter)> assistantToggleBindings,
        Action<string> log,
        Func<ChatAssistantViewModel.HostSnapshot> hostSnapshotProvider,
        Func<string> resolveBaseOutputFolder,
        Func<string> getOutputFolder,
        Func<IReadOnlyList<string>> getLogsSnapshot,
        Func<bool> canExecuteRunCommand,
        Action executeRunCommand,
        Func<bool> isRunInProgress,
        Action<RunPlan> enqueueRunPlan,
        Func<string?> getLatestStoreOutputFolder,
        Func<string?> getLatestManualBundlePath,
        Func<string?> getLatestManualReportPath,
        Func<string?> getLatestRunDeltaPath,
        Func<string?> getLatestRunAiBriefPath,
        Func<string?> getLatestRunSnapshotJson,
        Action<Action> invokeOnUiThread,
        Func<bool> getEnableHttpRetries,
        Action<bool> setEnableHttpRetries,
        Func<int> getHttpRetryAttempts,
        Action<int> setHttpRetryAttempts,
        Func<double> getHttpRetryBaseDelaySeconds,
        Action<double> setHttpRetryBaseDelaySeconds,
        Func<double> getHttpRetryMaxDelaySeconds,
        Action<double> setHttpRetryMaxDelaySeconds)
    {
        if (chatAssistant is null)
        {
            throw new ArgumentNullException(nameof(chatAssistant));
        }

        if (assistantToggleBindings is null)
        {
            throw new ArgumentNullException(nameof(assistantToggleBindings));
        }

        if (log is null)
        {
            throw new ArgumentNullException(nameof(log));
        }

        if (hostSnapshotProvider is null)
        {
            throw new ArgumentNullException(nameof(hostSnapshotProvider));
        }

        if (resolveBaseOutputFolder is null)
        {
            throw new ArgumentNullException(nameof(resolveBaseOutputFolder));
        }

        if (getOutputFolder is null)
        {
            throw new ArgumentNullException(nameof(getOutputFolder));
        }

        if (getLogsSnapshot is null)
        {
            throw new ArgumentNullException(nameof(getLogsSnapshot));
        }

        if (canExecuteRunCommand is null)
        {
            throw new ArgumentNullException(nameof(canExecuteRunCommand));
        }

        if (executeRunCommand is null)
        {
            throw new ArgumentNullException(nameof(executeRunCommand));
        }

        if (isRunInProgress is null)
        {
            throw new ArgumentNullException(nameof(isRunInProgress));
        }

        if (enqueueRunPlan is null)
        {
            throw new ArgumentNullException(nameof(enqueueRunPlan));
        }

        if (getLatestStoreOutputFolder is null)
        {
            throw new ArgumentNullException(nameof(getLatestStoreOutputFolder));
        }

        if (getLatestManualBundlePath is null)
        {
            throw new ArgumentNullException(nameof(getLatestManualBundlePath));
        }

        if (getLatestManualReportPath is null)
        {
            throw new ArgumentNullException(nameof(getLatestManualReportPath));
        }

        if (getLatestRunDeltaPath is null)
        {
            throw new ArgumentNullException(nameof(getLatestRunDeltaPath));
        }

        if (getLatestRunAiBriefPath is null)
        {
            throw new ArgumentNullException(nameof(getLatestRunAiBriefPath));
        }

        if (getLatestRunSnapshotJson is null)
        {
            throw new ArgumentNullException(nameof(getLatestRunSnapshotJson));
        }

        if (invokeOnUiThread is null)
        {
            throw new ArgumentNullException(nameof(invokeOnUiThread));
        }

        if (getEnableHttpRetries is null)
        {
            throw new ArgumentNullException(nameof(getEnableHttpRetries));
        }

        if (setEnableHttpRetries is null)
        {
            throw new ArgumentNullException(nameof(setEnableHttpRetries));
        }

        if (getHttpRetryAttempts is null)
        {
            throw new ArgumentNullException(nameof(getHttpRetryAttempts));
        }

        if (setHttpRetryAttempts is null)
        {
            throw new ArgumentNullException(nameof(setHttpRetryAttempts));
        }

        if (getHttpRetryBaseDelaySeconds is null)
        {
            throw new ArgumentNullException(nameof(getHttpRetryBaseDelaySeconds));
        }

        if (setHttpRetryBaseDelaySeconds is null)
        {
            throw new ArgumentNullException(nameof(setHttpRetryBaseDelaySeconds));
        }

        if (getHttpRetryMaxDelaySeconds is null)
        {
            throw new ArgumentNullException(nameof(getHttpRetryMaxDelaySeconds));
        }

        if (setHttpRetryMaxDelaySeconds is null)
        {
            throw new ArgumentNullException(nameof(setHttpRetryMaxDelaySeconds));
        }

        var hostConfiguration = new ChatAssistantViewModel.HostConfiguration(
            log,
            assistantToggleBindings,
            hostSnapshotProvider,
            resolveBaseOutputFolder,
            getOutputFolder,
            getLogsSnapshot,
            canExecuteRunCommand,
            executeRunCommand,
            isRunInProgress,
            enqueueRunPlan,
            getLatestStoreOutputFolder,
            getLatestManualBundlePath,
            getLatestManualReportPath,
            getLatestRunDeltaPath,
            getLatestRunAiBriefPath,
            getLatestRunSnapshotJson,
            invokeOnUiThread,
            getEnableHttpRetries,
            setEnableHttpRetries,
            getHttpRetryAttempts,
            setHttpRetryAttempts,
            getHttpRetryBaseDelaySeconds,
            setHttpRetryBaseDelaySeconds,
            getHttpRetryMaxDelaySeconds,
            setHttpRetryMaxDelaySeconds);

        chatAssistant.ConfigureHost(hostConfiguration);
    }

    public void HandleChatAssistantPropertyChanged(
        PropertyChangedEventArgs? e,
        Action savePreferences,
        Action? refreshLaunchWizard,
        Action? refreshExplainLogs)
    {
        if (e?.PropertyName is null)
        {
            return;
        }

        if (savePreferences is null)
        {
            throw new ArgumentNullException(nameof(savePreferences));
        }

        if (s_chatPreferencePropertyNames.Contains(e.PropertyName))
        {
            savePreferences();
        }

        if (e.PropertyName == nameof(ChatAssistantViewModel.ChatApiEndpoint)
            || e.PropertyName == nameof(ChatAssistantViewModel.ChatModel)
            || e.PropertyName == nameof(ChatAssistantViewModel.HasChatApiKey))
        {
            refreshLaunchWizard?.Invoke();
        }

        if (e.PropertyName == nameof(ChatAssistantViewModel.HasChatConfiguration))
        {
            refreshExplainLogs?.Invoke();
        }
    }
}
