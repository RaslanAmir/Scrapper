using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using WcScraper.Core;
using WcScraper.Wpf.Models;
using WcScraper.Wpf.Reporting;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf.Services;

public sealed class ChatAssistantService
{
    private const int DefaultToolCallIterationLimit = 8;

    private static readonly JsonSerializerOptions s_artifactPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_scriptPayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IArtifactIndexingService _artifactIndexingService;

    public ChatAssistantService()
        : this(new ArtifactIndexingService())
    {
    }

    public ChatAssistantService(IArtifactIndexingService artifactIndexingService)
    {
        _artifactIndexingService = artifactIndexingService ?? throw new ArgumentNullException(nameof(artifactIndexingService));
    }

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        ChatSessionSettings settings,
        IReadOnlyList<ChatMessage> history,
        string contextSummary,
        ChatAssistantToolbox? toolbox = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var promptBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            promptBuilder.AppendLine(settings.SystemPrompt.Trim());
        }
        else
        {
            promptBuilder.AppendLine("You are a helpful assistant for WC Local Scraper users.");
        }

        if (!string.IsNullOrWhiteSpace(contextSummary))
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("Context:");
            promptBuilder.AppendLine(contextSummary.Trim());
        }

        var requestMessages = new List<ChatRequestMessage>
        {
            new ChatRequestSystemMessage(promptBuilder.ToString())
        };

        foreach (var message in history)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (TryCreateRequestMessage(message, out var requestMessage))
            {
                requestMessages.Add(requestMessage);
            }
        }

        var toolCallIterations = 0;
        var maxToolCallIterations = settings.ToolCallIterationLimit.HasValue && settings.ToolCallIterationLimit.Value > 0
            ? settings.ToolCallIterationLimit.Value
            : DefaultToolCallIterationLimit;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasRemainingBudget(settings, out var budgetWarning))
            {
                if (!string.IsNullOrEmpty(budgetWarning))
                {
                    settings.DiagnosticLogger?.Invoke(budgetWarning);
                }

                yield break;
            }

            var options = new ChatCompletionsOptions
            {
                DeploymentName = settings.Model,
            };

            foreach (var message in requestMessages)
            {
                options.Messages.Add(message);
            }

            if (toolbox is not null)
            {
                foreach (var tool in toolbox.ToolDefinitions)
                {
                    options.Tools.Add(tool);
                }
            }

            var response = await client.GetChatCompletionsStreamingAsync(options, cancellationToken).ConfigureAwait(false);
            await using var streaming = response;

            var finishReason = default(CompletionsFinishReason?);
            var encounteredToolCalls = false;
            Dictionary<int, ToolCallState>? toolCallStates = toolbox is null ? null : new Dictionary<int, ToolCallState>();

            await foreach (var update in streaming.WithCancellation(cancellationToken))
            {
                if (toolbox is not null && update.ToolCallUpdate is StreamingFunctionToolCallUpdate functionToolCall)
                {
                    encounteredToolCalls = true;
                    var state = GetOrCreateToolState(toolCallStates!, functionToolCall.ToolCallIndex);
                    if (!string.IsNullOrEmpty(functionToolCall.Id))
                    {
                        state.Id = functionToolCall.Id;
                    }

                    if (!string.IsNullOrEmpty(functionToolCall.Name))
                    {
                        state.Name = functionToolCall.Name;
                    }

                    var chunk = functionToolCall.ArgumentsUpdate?.ToString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        state.Arguments.Append(chunk);
                    }

                    continue;
                }

                if (toolbox is not null && !string.IsNullOrEmpty(update.FunctionName))
                {
                    encounteredToolCalls = true;
                    var state = GetOrCreateToolState(toolCallStates!, 0);
                    state.Name ??= update.FunctionName;
                    var chunk = update.FunctionArgumentsUpdate?.ToString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        state.Arguments.Append(chunk);
                    }

                    continue;
                }

                if (update.ContentUpdate is { Count: > 0 } contentParts)
                {
                    foreach (var part in contentParts)
                    {
                        if (part is ChatMessageTextContentItem textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            yield return textContent.Text;
                        }
                    }
                }

                if (update.FinishReason is { } reason)
                {
                    finishReason = reason;
                }
            }

            var completions = await client
                .GetChatCompletionsAsync(options, cancellationToken)
                .ConfigureAwait(false);

            var usageSnapshot = CreateUsageSnapshot(completions.Value.Usage);
            ReportUsage(settings, usageSnapshot);

            if (toolbox is null || !encounteredToolCalls)
            {
                break;
            }

            if (finishReason != CompletionsFinishReason.ToolCalls || toolCallStates is null || toolCallStates.Count == 0)
            {
                break;
            }

            var orderedCalls = toolCallStates
                .Values
                .OrderBy(state => state.Index)
                .ToList();

            var assistantToolMessage = new ChatRequestAssistantMessage(string.Empty);
            var toolMessages = new List<ChatRequestToolMessage>();

            foreach (var call in orderedCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Name))
                {
                    continue;
                }

                var toolId = string.IsNullOrWhiteSpace(call.Id) ? $"tool_{call.Index}" : call.Id!;
                var arguments = call.Arguments.ToString();
                assistantToolMessage.ToolCalls.Add(new ChatCompletionsFunctionToolCall(toolId, call.Name!, arguments));

                var result = await toolbox.InvokeAsync(call.Name, arguments, cancellationToken).ConfigureAwait(false);
                toolMessages.Add(new ChatRequestToolMessage(toolId, result));
            }

            if (assistantToolMessage.ToolCalls.Count == 0)
            {
                break;
            }

            requestMessages.Add(assistantToolMessage);
            foreach (var toolMessage in toolMessages)
            {
                requestMessages.Add(toolMessage);
            }

            toolCallIterations++;
            if (toolCallIterations >= maxToolCallIterations)
            {
                var cycleText = toolCallIterations == 1 ? "cycle" : "cycles";
                var diagnostic = $"Assistant stopped after reaching the tool-call iteration limit ({toolCallIterations} {cycleText}).";
                settings.DiagnosticLogger?.Invoke(diagnostic);
                yield return diagnostic;
                break;
            }
        }
    }

    public async IAsyncEnumerable<string> StreamDatasetAnswerAsync(
        ChatSessionSettings settings,
        IReadOnlyList<ChatMessage> history,
        string latestQuestion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(history);

        if (string.IsNullOrWhiteSpace(latestQuestion))
        {
            yield break;
        }

        if (!_artifactIndexingService.HasAnyIndexedArtifacts)
        {
            yield return "No indexed exports are available. Run a scrape with CSV or JSONL exports to enable this mode.";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var matches = await _artifactIndexingService
            .SearchAsync(latestQuestion, take: 8, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            yield return "I could not find any relevant rows in the indexed exports for that question.";
            yield break;
        }

        var datasetInventory = _artifactIndexingService.GetIndexedDatasets();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));
        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.1f,
        };

        var systemPrompt = BuildDatasetSystemPrompt(settings.SystemPrompt, datasetInventory, matches, settings.DiagnosticLogger);
        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt));

        foreach (var message in history)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (TryCreateRequestMessage(message, out var requestMessage))
            {
                options.Messages.Add(requestMessage);
            }
        }

        if (!HasRemainingBudget(settings, out var datasetBudgetWarning))
        {
            if (!string.IsNullOrEmpty(datasetBudgetWarning))
            {
                settings.DiagnosticLogger?.Invoke(datasetBudgetWarning);
            }

            yield break;
        }

        var response = await client.GetChatCompletionsStreamingAsync(options, cancellationToken).ConfigureAwait(false);
        await using var streaming = response;

        await foreach (var update in streaming.WithCancellation(cancellationToken))
        {
            if (update.ContentUpdate is { Count: > 0 } contentParts)
            {
                foreach (var part in contentParts)
                {
                    if (part is ChatMessageTextContentItem textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        yield return textContent.Text;
                    }
                }
            }
        }

        var completions = await client
            .GetChatCompletionsAsync(options, cancellationToken)
            .ConfigureAwait(false);

        var usageSnapshot = CreateUsageSnapshot(completions.Value.Usage);
        ReportUsage(settings, usageSnapshot);
    }

    public async Task<AssistantDirectiveBatch?> ParseAssistantDirectivesAsync(
        ChatSessionSettings settings,
        ChatSessionContext context,
        string latestUserMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(latestUserMessage))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.1f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You translate WC Local Scraper operator requests into structured directives.");
        systemPrompt.AppendLine("Respond ONLY with minified JSON matching this schema:");
        systemPrompt.AppendLine("{");
        systemPrompt.AppendLine("  \"summary\": \"human readable headline\",");
        systemPrompt.AppendLine("  \"changes\": [");
        systemPrompt.AppendLine("    {\"type\": \"toggle\", \"name\": \"ExportPublicDesignSnapshot\", \"value\": true, \"justification\": \"why\", \"risk_level\": \"low|medium|high\", \"confidence\": 0.8, \"requires_confirmation\": false}");
        systemPrompt.AppendLine("  ],");
        systemPrompt.AppendLine("  \"retry\": {\"enable\": true, \"attempts\": 4, \"base_delay_seconds\": 1.5, \"max_delay_seconds\": 40, \"justification\": \"why\"},");
        systemPrompt.AppendLine("  \"credential_reminders\": [{\"credential\": \"WordPress\", \"message\": \"Ask for admin password\"}],");
        systemPrompt.AppendLine("  \"actions\": [");
        systemPrompt.AppendLine("    \"start_run\",");
        systemPrompt.AppendLine("    {\"type\": \"schedule_run\", \"requires_confirmation\": true, \"plan\": {\"run_name\": \"Follow-up remediation\", \"execution_order\": 2, \"timing\": \"scheduled\", \"scheduled_for_utc\": \"2024-05-01T02:00:00Z\", \"settings_overrides\": [{\"name\": \"ExportPublicDesignSnapshot\", \"value\": true, \"description\": \"Capture design HTML before re-run\"}], \"prerequisites\": [\"Confirm credentials\"], \"summary\": \"Queue a remediation run with design capture\"}}");
        systemPrompt.AppendLine("  ],");
        systemPrompt.AppendLine("  \"requires_confirmation\": false,");
        systemPrompt.AppendLine("  \"risk_note\": \"summarize any risks\"");
        systemPrompt.AppendLine("}");
        systemPrompt.AppendLine("If there are no actionable changes, return empty arrays and false flags.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var contextPrompt = BuildContextualPrompt(context);

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Latest operator message:");
        userPrompt.AppendLine(latestUserMessage.Trim());
        userPrompt.AppendLine();
        userPrompt.AppendLine("Current session context:");
        userPrompt.AppendLine(contextPrompt.Trim());
        userPrompt.AppendLine();
        userPrompt.AppendLine("Return JSON only. Do not include markdown fences.");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return ParseAssistantDirectivePayload(content);
    }

    public async Task<LogTriageResult?> SummarizeLogsAsync(
        ChatSessionSettings settings,
        IReadOnlyList<string> logEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logEntries);

        if (logEntries.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var snippet = logEntries
            .Skip(Math.Max(0, logEntries.Count - 80))
            .Select(entry => entry.TrimEnd())
            .ToArray();

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.2f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You triage diagnostic logs for WC Local Scraper users.");
        systemPrompt.AppendLine("Highlight real issues, classify them, and suggest practical next steps.");
        systemPrompt.AppendLine("Respond with concise and actionable guidance.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var logPayload = string.Join(Environment.NewLine, snippet);
        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Analyze the following log excerpt and identify anything the operator should address.");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Return a JSON object with this shape:");
        userPrompt.AppendLine("{");
        userPrompt.AppendLine("  \"overallSummary\": \"short headline\",");
        userPrompt.AppendLine("  \"issues\": [");
        userPrompt.AppendLine("    {\"category\": \"area\", \"severity\": \"info|warning|error|critical\", \"description\": \"what happened\", \"recommendation\": \"what to do\"}");
        userPrompt.AppendLine("  ]");
        userPrompt.AppendLine("}");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Only include issues that add value. If nothing is wrong, set issues to an empty array and explain that the logs look healthy.");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Logs:");
        userPrompt.AppendLine("```");
        userPrompt.AppendLine(logPayload);
        userPrompt.AppendLine("```");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var message = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var parsed = ParseLogTriage(message);
        if (parsed is null)
        {
            return new LogTriageResult(DateTimeOffset.UtcNow, message.Trim(), Array.Empty<LogTriageIssue>());
        }

        return new LogTriageResult(DateTimeOffset.UtcNow, parsed.Value.Overview, parsed.Value.Issues);
    }

    public async Task<string?> SummarizeRunAsync(
        ChatSessionSettings settings,
        string runSnapshotJson,
        string? operatorGoals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(runSnapshotJson))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.4f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You are an AI assistant that prepares migration briefs for WooCommerce and Shopify storefront audits.");
        systemPrompt.AppendLine("Focus on risks, manual work items, and opportunities surfaced by the run.");
        systemPrompt.AppendLine("Respond in clear Markdown with short sections and actionable recommendations.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Craft a narrative summary of the latest run, highlighting what a human operator should do next.");
        userPrompt.AppendLine("Cover noteworthy plugins, themes, public extension signals, and design considerations.");
        userPrompt.AppendLine("Close with specific follow-up actions or questions for the operator.");

        var goals = operatorGoals?.Trim();
        if (!string.IsNullOrWhiteSpace(goals))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Operator goals:");
            userPrompt.AppendLine(goals);
        }

        userPrompt.AppendLine();
        userPrompt.AppendLine("Run snapshot (JSON):");
        userPrompt.AppendLine("```json");
        userPrompt.AppendLine(runSnapshotJson.Trim());
        userPrompt.AppendLine("```");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var content = choice?.Message?.Content;
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    public async Task<ExportVerificationResult?> VerifyExportsAsync(
        ChatSessionSettings settings,
        ManualMigrationReportContext context,
        IReadOnlyList<ManualMigrationDirectorySnapshot>? outputDirectories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var entityCounts = context.EntityCounts is null
            ? null
            : new
            {
                products = context.EntityCounts.ProductCount,
                orders = context.EntityCounts.OrderCount,
                media = context.EntityCounts.MediaItemCount,
                designAssets = context.EntityCounts.DesignAssetCount,
                publicSlugs = context.EntityCounts.PublicSlugCount,
            };

        var directories = (outputDirectories ?? Array.Empty<ManualMigrationDirectorySnapshot>())
            .Select(directory => new
            {
                relativePath = directory.RelativePath,
                absolutePath = directory.AbsolutePath,
                files = directory.FileCount,
                bytes = directory.TotalSizeBytes,
            })
            .ToArray();

        var fileSystem = context.FileSystemStats is null
            ? null
            : new
            {
                totalFiles = context.FileSystemStats.TotalFileCount,
                totalBytes = context.FileSystemStats.TotalSizeBytes,
            };

        var logTail = context.LogEntries.Count == 0
            ? Array.Empty<string>()
            : context.LogEntries
                .Skip(Math.Max(0, context.LogEntries.Count - 10))
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToArray();

        var payload = new
        {
            generatedAtUtc = context.GeneratedAtUtc,
            store = new
            {
                context.StoreIdentifier,
                context.StoreUrl,
                context.IsWooCommerce,
            },
            requests = new
            {
                context.RequestedPluginInventory,
                context.RequestedThemeInventory,
                context.RequestedPublicExtensionFootprints,
                context.RequestedDesignSnapshot,
                context.RequestedDesignScreenshots,
            },
            attempts = new
            {
                context.AttemptedPluginFetch,
                context.AttemptedThemeFetch,
                context.AttemptedPublicExtensionFootprintFetch,
            },
            entityCounts,
            exportInventories = new
            {
                plugins = context.Plugins?.Count ?? 0,
                themes = context.Themes?.Count ?? 0,
                publicExtensions = context.PublicExtensions?.Count ?? 0,
                pluginBundles = context.PluginBundles?.Count ?? 0,
                themeBundles = context.ThemeBundles?.Count ?? 0,
                automationScripts = context.AutomationScripts?.Scripts.Count ?? 0,
                designScreenshots = context.DesignScreenshots?.Count ?? 0,
            },
            fileSystem,
            directories,
            missingCredentials = context.MissingCredentialExports,
            logTail,
            httpRetries = new
            {
                context.HttpRetriesEnabled,
                context.HttpRetryAttempts,
                baseDelaySeconds = Math.Round(context.HttpRetryBaseDelay.TotalSeconds, 3),
                maxDelaySeconds = Math.Round(context.HttpRetryMaxDelay.TotalSeconds, 3),
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload, s_artifactPayloadOptions);

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.1f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You verify WC Local Scraper export runs for completeness and health.");
        systemPrompt.AppendLine("Cross-check entity counts against captured files and call out suspiciously small datasets.");
        systemPrompt.AppendLine("Look for missing exports, mismatched totals, or directories that appear truncated.");
        systemPrompt.AppendLine("Respond ONLY with compact JSON using this shape:");
        systemPrompt.AppendLine("{");
        systemPrompt.AppendLine("  \"summary\": \"status headline\",");
        systemPrompt.AppendLine("  \"issues\": [{\"severity\": \"critical|warning|info\", \"title\": \"short label\", \"description\": \"details\", \"recommendation\": \"next step\"}],");
        systemPrompt.AppendLine("  \"suggested_fixes\": [\"operator action\"],");
        systemPrompt.AppendLine("  \"suggested_directives\": { ... optional WC Local Scraper directive payload ... }");
        systemPrompt.AppendLine("}");
        systemPrompt.AppendLine("If there are no concerns, return an empty issues array and a confident summary.");
        systemPrompt.AppendLine("Only include suggested_directives when configuration changes would help.");
        systemPrompt.AppendLine("Do not wrap the response in markdown fences.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Review the latest export metrics and directory inventory.");
        userPrompt.AppendLine("Identify missing files, mismatched counts, or unusually small datasets that could block a migration.");
        userPrompt.AppendLine("Suggest concrete follow-up actions for any problems you find.");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Run context (JSON):");
        userPrompt.AppendLine("```json");
        userPrompt.AppendLine(payloadJson);
        userPrompt.AppendLine("```");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var content = choice?.Message?.Content;
        return ParseExportVerificationPayload(content);
    }

    public async Task<string?> SummarizeRunDeltaAsync(
        ChatSessionSettings settings,
        string runDeltaJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(runDeltaJson))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.2f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You compare consecutive WC Local Scraper runs and explain what changed.");
        systemPrompt.AppendLine("Call out new risks, resolved issues, and manual follow-ups for migration operators.");
        systemPrompt.AppendLine("Respond in Markdown with concise sections and bullet lists.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Summarize the delta between the latest run and the previous baseline.");
        userPrompt.AppendLine("Highlight plugin/theme changes, public extension updates, design asset shifts, automation warnings, and missing credentials or log notes.");
        userPrompt.AppendLine("Identify newly emerged concerns and call out resolved items where applicable.");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Run delta (JSON):");
        userPrompt.AppendLine("```json");
        userPrompt.AppendLine(runDeltaJson.Trim());
        userPrompt.AppendLine("```");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var content = choice?.Message?.Content;
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    public async Task<MigrationScriptGenerationResult> GenerateMigrationScriptsAsync(
        ChatSessionSettings settings,
        string runSnapshotJson,
        string? operatorGoals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(runSnapshotJson))
        {
            return MigrationScriptGenerationResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.1f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You turn storefront migration run data into executable automation snippets.");
        systemPrompt.AppendLine("Output practical WP-CLI commands, shell scripts, or REST examples that accelerate manual work.");
        systemPrompt.AppendLine("Respond ONLY with compact JSON using this schema:");
        systemPrompt.AppendLine("{");
        systemPrompt.AppendLine("  \"summary\": \"high level plan\",");
        systemPrompt.AppendLine("  \"warnings\": [\"cautionary note\"],");
        systemPrompt.AppendLine("  \"error\": \"explain fatal issues or null\",");
        systemPrompt.AppendLine("  \"scripts\": [");
        systemPrompt.AppendLine("    {\"name\": \"WP-CLI install\", \"description\": \"what it does\", \"language\": \"shell|wp-cli|powershell|rest|http|python\", \"filename\": \"optional-file-name.sh\", \"content\": \"script body\", \"notes\": [\"optional tip\"]}");
        systemPrompt.AppendLine("  ]");
        systemPrompt.AppendLine("}");
        systemPrompt.AppendLine("Return an empty scripts array if nothing actionable exists. Do not use markdown fences.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Generate 1-3 concise automation snippets that help an operator execute the migration goals.");
        userPrompt.AppendLine("Prefer idempotent commands that can be run safely. Include WP-CLI install/activate steps when useful.");
        userPrompt.AppendLine("Call out prerequisites (e.g., credentials, directories) in the notes field.");

        var goals = operatorGoals?.Trim();
        if (!string.IsNullOrWhiteSpace(goals))
        {
            userPrompt.AppendLine();
            userPrompt.AppendLine("Operator goals:");
            userPrompt.AppendLine(goals);
        }

        userPrompt.AppendLine();
        userPrompt.AppendLine("Run snapshot (JSON):");
        userPrompt.AppendLine("```json");
        userPrompt.AppendLine(runSnapshotJson.Trim());
        userPrompt.AppendLine("```");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Return JSON only.");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var content = choice?.Message?.Content;
        return ParseAutomationScriptPayload(content);
    }

    public async Task<AiArtifactAnnotation?> AnnotateArtifactsAsync(
        ChatSessionSettings settings,
        AiArtifactIntelligencePayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.HasContent)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new ArgumentException("An API key is required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new ArgumentException("An endpoint is required.", nameof(settings));
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("The API endpoint must be an absolute URI.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new ArgumentException("A model or deployment identifier is required.", nameof(settings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var client = new OpenAIClient(endpoint, new AzureKeyCredential(settings.ApiKey));

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
            Temperature = 0.3f,
        };

        var systemPrompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemPrompt.AppendLine(settings.SystemPrompt.Trim());
            systemPrompt.AppendLine();
        }

        systemPrompt.AppendLine("You review WooCommerce storefront artifacts and highlight migration considerations.");
        systemPrompt.AppendLine("Call out public plugin clues, likely extension purposes, and design rebuild notes.");
        systemPrompt.AppendLine("Respond with concise, actionable guidance that references concrete files or slugs when possible.");

        options.Messages.Add(new ChatRequestSystemMessage(systemPrompt.ToString()));

        var payloadJson = JsonSerializer.Serialize(payload, s_artifactPayloadOptions);

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Analyze the storefront artifacts payload and propose migration recommendations.");
        userPrompt.AppendLine("Respond ONLY with JSON matching this schema:");
        userPrompt.AppendLine("{");
        userPrompt.AppendLine("  \"markdown_summary\": \"overview in markdown\",");
        userPrompt.AppendLine("  \"recommendations\": [");
        userPrompt.AppendLine("    {\"title\": \"short label\", \"summary\": \"actionable details\", \"suggested_prompts\": [\"follow-up prompt\"], \"related_assets\": [\"slug or file\"]}");
        userPrompt.AppendLine("  ]");
        userPrompt.AppendLine("}");
        userPrompt.AppendLine("If there are no recommendations, return an empty array for recommendations.");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Artifacts payload:");
        userPrompt.AppendLine("```json");
        userPrompt.AppendLine(payloadJson);
        userPrompt.AppendLine("```");

        options.Messages.Add(new ChatRequestUserMessage(userPrompt.ToString()));

        var response = await client.GetChatCompletionsAsync(options, cancellationToken).ConfigureAwait(false);
        var choice = response.Value.Choices.FirstOrDefault();
        var content = choice?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var annotation = ParseArtifactAnnotation(content);
        if (annotation is null)
        {
            return null;
        }

        var summary = string.IsNullOrWhiteSpace(annotation.MarkdownSummary)
            ? string.Empty
            : annotation.MarkdownSummary.Trim();

        if (string.IsNullOrWhiteSpace(summary) && !annotation.HasRecommendations)
        {
            return null;
        }

        return annotation with { MarkdownSummary = summary };
    }

    public string BuildContextualPrompt(ChatSessionContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Selected platform: {context.SelectedPlatform}");
        builder.AppendLine("Exports:");
        builder.AppendLine($"  Generic CSV: {FormatBoolean(context.ExportCsv)}");
        builder.AppendLine($"  Shopify CSV: {FormatBoolean(context.ExportShopify)}");
        builder.AppendLine($"  WooCommerce CSV: {FormatBoolean(context.ExportWoo)}");
        builder.AppendLine($"  Reviews CSV: {FormatBoolean(context.ExportReviews)}");
        builder.AppendLine($"  XLSX: {FormatBoolean(context.ExportXlsx)}");
        builder.AppendLine($"  JSONL: {FormatBoolean(context.ExportJsonl)}");
        builder.AppendLine($"  Plugins CSV: {FormatBoolean(context.ExportPluginsCsv)}");
        builder.AppendLine($"  Plugins JSONL: {FormatBoolean(context.ExportPluginsJsonl)}");
        builder.AppendLine($"  Themes CSV: {FormatBoolean(context.ExportThemesCsv)}");
        builder.AppendLine($"  Themes JSONL: {FormatBoolean(context.ExportThemesJsonl)}");
        builder.AppendLine($"  Public extension footprints: {FormatBoolean(context.ExportPublicExtensionFootprints)}");
        builder.AppendLine($"  Public design snapshot: {FormatBoolean(context.ExportPublicDesignSnapshot)}");
        builder.AppendLine($"  Public design screenshots: {FormatBoolean(context.ExportPublicDesignScreenshots)}");
        builder.AppendLine($"  Store configuration export: {FormatBoolean(context.ExportStoreConfiguration)}");
        builder.AppendLine($"  Apply configuration during provisioning: {FormatBoolean(context.ImportStoreConfiguration)}");

        builder.AppendLine();
        builder.AppendLine("Credentials:");
        builder.AppendLine($"  WordPress admin credentials supplied: {FormatBoolean(context.HasWordPressCredentials)}");
        builder.AppendLine($"  Shopify tokens supplied: {FormatBoolean(context.HasShopifyCredentials)}");
        builder.AppendLine($"  Target WooCommerce credentials supplied: {FormatBoolean(context.HasTargetCredentials)}");

        builder.AppendLine();
        builder.AppendLine($"HTTP retries enabled: {FormatBoolean(context.EnableHttpRetries)} (attempts: {context.HttpRetryAttempts})");

        if (!string.IsNullOrWhiteSpace(context.AdditionalPublicExtensionPages))
        {
            builder.AppendLine();
            builder.AppendLine("Additional extension pages:");
            builder.AppendLine(context.AdditionalPublicExtensionPages.Trim());
        }

        if (!string.IsNullOrWhiteSpace(context.AdditionalDesignSnapshotPages))
        {
            builder.AppendLine();
            builder.AppendLine("Additional design snapshot pages:");
            builder.AppendLine(context.AdditionalDesignSnapshotPages.Trim());
        }

        if (context.IndexedDatasetCount > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Indexed datasets available: {context.IndexedDatasetCount}");
            foreach (var name in context.IndexedDatasetNames)
            {
                builder.AppendLine($"  - {name}");
            }
        }

        return builder.ToString();
    }

    internal static AssistantDirectiveBatch? ParseAssistantDirectivePayload(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var jsonText = ExtractJsonPayload(response);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            var summary = root.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString()
                : null;
            var requiresConfirmation = root.TryGetProperty("requires_confirmation", out var confirmationElement)
                && confirmationElement.ValueKind == JsonValueKind.True;
            var riskNote = root.TryGetProperty("risk_note", out var riskNoteElement)
                ? riskNoteElement.GetString()
                : null;

            var toggles = new List<AssistantToggleDirective>();
            var actions = new List<AssistantActionDirective>();
            if (root.TryGetProperty("changes", out var changesElement) && changesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var changeElement in changesElement.EnumerateArray())
                {
                    var type = changeElement.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : null;
                    if (!string.Equals(type, "toggle", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = changeElement.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    bool? value = null;
                    if (changeElement.TryGetProperty("value", out var valueElement))
                    {
                        value = valueElement.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String when bool.TryParse(valueElement.GetString(), out var parsedBool) => parsedBool,
                            _ => null,
                        };
                    }

                    if (value is null)
                    {
                        continue;
                    }

                    string? justification = changeElement.TryGetProperty("justification", out var justificationElement)
                        ? justificationElement.GetString()
                        : null;
                    string? riskLevel = changeElement.TryGetProperty("risk_level", out var riskElement)
                        ? riskElement.GetString()
                        : null;

                    double? confidence = null;
                    if (changeElement.TryGetProperty("confidence", out var confidenceElement))
                    {
                        confidence = confidenceElement.ValueKind switch
                        {
                            JsonValueKind.Number when confidenceElement.TryGetDouble(out var parsedConfidence) => parsedConfidence,
                            JsonValueKind.String when double.TryParse(confidenceElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedConfidence) => parsedConfidence,
                            _ => null,
                        };
                    }

                    var toggleRequiresConfirmation = changeElement.TryGetProperty("requires_confirmation", out var toggleConfirmationElement)
                        && toggleConfirmationElement.ValueKind == JsonValueKind.True;

                    toggles.Add(new AssistantToggleDirective(
                        name.Trim(),
                        value.Value,
                        string.IsNullOrWhiteSpace(justification) ? null : justification.Trim(),
                        string.IsNullOrWhiteSpace(riskLevel) ? null : riskLevel.Trim(),
                        confidence,
                        toggleRequiresConfirmation));
                }
            }

            if (root.TryGetProperty("actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var actionElement in actionsElement.EnumerateArray())
                {
                    string? actionName = actionElement.ValueKind switch
                    {
                        JsonValueKind.String => actionElement.GetString(),
                        JsonValueKind.Object when actionElement.TryGetProperty("name", out var nameElement) && !string.IsNullOrWhiteSpace(nameElement.GetString()) => nameElement.GetString(),
                        JsonValueKind.Object when actionElement.TryGetProperty("type", out var typeElement) => typeElement.GetString(),
                        _ => null,
                    };

                    if (string.IsNullOrWhiteSpace(actionName))
                    {
                        continue;
                    }

                    string? justification = null;
                    var actionRequiresConfirmation = false;
                    RunPlan? runPlan = null;
                    if (actionElement.ValueKind == JsonValueKind.Object)
                    {
                        if (actionElement.TryGetProperty("justification", out var justificationElement))
                        {
                            justification = justificationElement.GetString();
                        }

                        actionRequiresConfirmation = actionElement.TryGetProperty("requires_confirmation", out var actionConfirmationElement)
                            && actionConfirmationElement.ValueKind == JsonValueKind.True;

                        runPlan = TryParseRunPlan(actionElement, summary);
                    }

                    var normalizedActionName = actionName.Trim();
                    if (string.Equals(normalizedActionName, "schedule_run", StringComparison.OrdinalIgnoreCase) && runPlan is null)
                    {
                        continue;
                    }

                    actions.Add(new AssistantActionDirective(
                        normalizedActionName,
                        string.IsNullOrWhiteSpace(justification) ? null : justification.Trim(),
                        actionRequiresConfirmation,
                        runPlan));
                }
            }

            AssistantRetryDirective? retryDirective = null;
            if (root.TryGetProperty("retry", out var retryElement) && retryElement.ValueKind == JsonValueKind.Object)
            {
                bool? enable = null;
                if (retryElement.TryGetProperty("enable", out var enableElement))
                {
                    enable = enableElement.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String when bool.TryParse(enableElement.GetString(), out var parsedEnable) => parsedEnable,
                        _ => null,
                    };
                }

                int? attempts = null;
                if (retryElement.TryGetProperty("attempts", out var attemptsElement))
                {
                    attempts = attemptsElement.ValueKind switch
                    {
                        JsonValueKind.Number when attemptsElement.TryGetInt32(out var parsedAttempts) => parsedAttempts,
                        JsonValueKind.String when int.TryParse(attemptsElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAttempts) => parsedAttempts,
                        _ => null,
                    };
                }

                double? baseDelay = null;
                if (retryElement.TryGetProperty("base_delay_seconds", out var baseDelayElement))
                {
                    baseDelay = baseDelayElement.ValueKind switch
                    {
                        JsonValueKind.Number when baseDelayElement.TryGetDouble(out var parsedBase) => parsedBase,
                        JsonValueKind.String when double.TryParse(baseDelayElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBase) => parsedBase,
                        _ => null,
                    };
                }

                double? maxDelay = null;
                if (retryElement.TryGetProperty("max_delay_seconds", out var maxDelayElement))
                {
                    maxDelay = maxDelayElement.ValueKind switch
                    {
                        JsonValueKind.Number when maxDelayElement.TryGetDouble(out var parsedMax) => parsedMax,
                        JsonValueKind.String when double.TryParse(maxDelayElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMax) => parsedMax,
                        _ => null,
                    };
                }

                string? justification = retryElement.TryGetProperty("justification", out var retryJustificationElement)
                    ? retryJustificationElement.GetString()
                    : null;

                if (enable is not null || attempts is not null || baseDelay is not null || maxDelay is not null)
                {
                    retryDirective = new AssistantRetryDirective(
                        enable,
                        attempts,
                        baseDelay,
                        maxDelay,
                        string.IsNullOrWhiteSpace(justification) ? null : justification.Trim());
                }
            }

            var reminders = new List<AssistantCredentialReminder>();
            if (root.TryGetProperty("credential_reminders", out var remindersElement) && remindersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var reminderElement in remindersElement.EnumerateArray())
                {
                    var credential = reminderElement.TryGetProperty("credential", out var credentialElement)
                        ? credentialElement.GetString()
                        : null;
                    var message = reminderElement.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    reminders.Add(new AssistantCredentialReminder(
                        string.IsNullOrWhiteSpace(credential) ? "General" : credential.Trim(),
                        message.Trim()));
                }
            }

            if (toggles.Count == 0 && retryDirective is null && reminders.Count == 0 && actions.Count == 0)
            {
                return null;
            }

            var normalizedSummary = string.IsNullOrWhiteSpace(summary)
                ? "Assistant directive update."
                : summary.Trim();
            var normalizedRiskNote = string.IsNullOrWhiteSpace(riskNote) ? null : riskNote.Trim();

            return new AssistantDirectiveBatch(
                normalizedSummary,
                toggles,
                retryDirective,
                actions,
                reminders,
                requiresConfirmation,
                normalizedRiskNote);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RunPlan? TryParseRunPlan(JsonElement actionElement, string? directiveSummary)
    {
        if (actionElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var hasPlanProperty = actionElement.TryGetProperty("plan", out var planElementCandidate)
            && planElementCandidate.ValueKind == JsonValueKind.Object;
        var typeLabel = actionElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var nameLabel = actionElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        var isScheduleRun = string.Equals(typeLabel, "schedule_run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nameLabel, "schedule_run", StringComparison.OrdinalIgnoreCase);

        if (!isScheduleRun)
        {
            return null;
        }

        var planElement = hasPlanProperty ? planElementCandidate : actionElement;
        if (planElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var runName = planElement.TryGetProperty("run_name", out var runNameElement)
            ? runNameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(runName) && planElement.TryGetProperty("name", out var altNameElement))
        {
            runName = altNameElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(runName))
        {
            runName = "Assistant remediation run";
        }

        var executionMode = RunPlanExecutionMode.Immediate;
        if (planElement.TryGetProperty("timing", out var timingElement))
        {
            var timing = timingElement.GetString();
            if (!string.IsNullOrWhiteSpace(timing)
                && timing.IndexOf("schedule", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                executionMode = RunPlanExecutionMode.Scheduled;
            }
        }

        bool? runImmediatelyFlag = null;
        if (planElement.TryGetProperty("run_immediately", out var runImmediatelyElement)
            && TryReadBool(runImmediatelyElement, out var runImmediately))
        {
            runImmediatelyFlag = runImmediately;
        }

        DateTimeOffset? scheduledForUtc = null;
        if (planElement.TryGetProperty("scheduled_for_utc", out var scheduledElement))
        {
            var scheduledText = scheduledElement.GetString();
            if (!string.IsNullOrWhiteSpace(scheduledText)
                && DateTimeOffset.TryParse(scheduledText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedScheduled))
            {
                scheduledForUtc = parsedScheduled;
            }
        }

        if (scheduledForUtc is null && TryReadDoubleProperty(planElement, "delay_seconds", out var delaySeconds))
        {
            scheduledForUtc = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
        }
        else if (scheduledForUtc is null && TryReadDoubleProperty(planElement, "delay_minutes", out var delayMinutes))
        {
            scheduledForUtc = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
        }

        if (runImmediatelyFlag is false || scheduledForUtc is not null)
        {
            executionMode = RunPlanExecutionMode.Scheduled;
        }

        var overrides = new List<RunPlanSettingOverride>();
        if (planElement.TryGetProperty("settings_overrides", out var overridesElement)
            && overridesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var overrideElement in overridesElement.EnumerateArray())
            {
                if (TryParseRunPlanOverride(overrideElement, out var setting))
                {
                    overrides.Add(setting);
                }
            }
        }

        var prerequisites = new List<string>();
        AppendPlanNotes(planElement, prerequisites, "prerequisites", "prerequisite_notes", "notes");

        int? executionOrder = null;
        if (TryReadIntProperty(planElement, "execution_order", out var parsedOrder))
        {
            executionOrder = parsedOrder;
        }

        var planSummary = directiveSummary;
        if (planElement.TryGetProperty("summary", out var planSummaryElement))
        {
            var summaryText = planSummaryElement.GetString();
            if (!string.IsNullOrWhiteSpace(summaryText))
            {
                planSummary = summaryText;
            }
        }

        return new RunPlan(
            Guid.NewGuid(),
            runName.Trim(),
            executionMode,
            DateTimeOffset.UtcNow,
            scheduledForUtc,
            overrides,
            prerequisites,
            string.IsNullOrWhiteSpace(planSummary) ? null : planSummary.Trim(),
            executionOrder);
    }

    private static bool TryParseRunPlanOverride(JsonElement overrideElement, out RunPlanSettingOverride setting)
    {
        setting = default!;
        if (overrideElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = overrideElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!overrideElement.TryGetProperty("value", out var valueElement))
        {
            return false;
        }

        var description = overrideElement.TryGetProperty("description", out var descriptionElement)
            ? descriptionElement.GetString()
            : null;

        RunPlanSettingValueKind? kind = null;
        bool? boolValue = null;
        double? numberValue = null;
        string? textValue = null;

        if (overrideElement.TryGetProperty("value_type", out var valueTypeElement))
        {
            var typeLabel = valueTypeElement.GetString();
            switch (typeLabel?.Trim().ToLowerInvariant())
            {
                case "boolean":
                case "bool":
                    if (TryReadBool(valueElement, out var parsedBool))
                    {
                        kind = RunPlanSettingValueKind.Boolean;
                        boolValue = parsedBool;
                    }
                    break;
                case "number":
                case "double":
                case "float":
                    if (TryReadDouble(valueElement, out var parsedNumber))
                    {
                        kind = RunPlanSettingValueKind.Number;
                        numberValue = parsedNumber;
                    }
                    break;
                case "text":
                case "string":
                    var parsedText = ExtractString(valueElement);
                    if (!string.IsNullOrWhiteSpace(parsedText))
                    {
                        kind = RunPlanSettingValueKind.Text;
                        textValue = parsedText;
                    }
                    break;
            }
        }

        if (kind is null)
        {
            if (TryReadBool(valueElement, out var fallbackBool))
            {
                kind = RunPlanSettingValueKind.Boolean;
                boolValue = fallbackBool;
            }
            else if (TryReadDouble(valueElement, out var fallbackNumber))
            {
                kind = RunPlanSettingValueKind.Number;
                numberValue = fallbackNumber;
            }
            else
            {
                var fallbackText = ExtractString(valueElement);
                if (!string.IsNullOrWhiteSpace(fallbackText))
                {
                    kind = RunPlanSettingValueKind.Text;
                    textValue = fallbackText;
                }
            }
        }

        if (kind is null)
        {
            return false;
        }

        setting = new RunPlanSettingOverride(
            name.Trim(),
            kind.Value,
            boolValue,
            numberValue,
            string.IsNullOrWhiteSpace(textValue) ? null : textValue,
            string.IsNullOrWhiteSpace(description) ? null : description?.Trim());

        return true;
    }

    private static void AppendPlanNotes(JsonElement planElement, List<string> destination, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!planElement.TryGetProperty(propertyName, out var noteElement))
            {
                continue;
            }

            switch (noteElement.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var entry in noteElement.EnumerateArray())
                    {
                        if (entry.ValueKind == JsonValueKind.String)
                        {
                            var text = entry.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                destination.Add(text.Trim());
                            }
                        }
                    }
                    break;
                case JsonValueKind.String:
                    var single = noteElement.GetString();
                    if (!string.IsNullOrWhiteSpace(single))
                    {
                        destination.Add(single.Trim());
                    }
                    break;
            }
        }
    }

    private static bool TryReadBool(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                if (bool.TryParse(element.GetString(), out var parsedStringBool))
                {
                    value = parsedStringBool;
                    return true;
                }
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue))
                {
                    value = intValue != 0;
                    return true;
                }
                break;
        }

        value = default;
        return false;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetDouble(out var number))
                {
                    value = number;
                    return true;
                }
                break;
            case JsonValueKind.String:
                if (double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNumber))
                {
                    value = parsedNumber;
                    return true;
                }
                break;
        }

        value = default;
        return false;
    }

    private static bool TryReadDoubleProperty(JsonElement element, string propertyName, out double value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var propertyElement))
        {
            return false;
        }

        return TryReadDouble(propertyElement, out value);
    }

    private static bool TryReadIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var propertyElement))
        {
            return false;
        }

        if (propertyElement.ValueKind == JsonValueKind.Number && propertyElement.TryGetInt32(out var numberValue))
        {
            value = numberValue;
            return true;
        }

        if (propertyElement.ValueKind == JsonValueKind.String
            && int.TryParse(propertyElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            value = parsedValue;
            return true;
        }

        return false;
    }

    private static string? ExtractString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetDouble(out var number) => number.ToString("0.###", CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static ExportVerificationResult? ParseExportVerificationPayload(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var jsonText = ExtractJsonPayload(response);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            var summary = root.TryGetProperty("summary", out var summaryElement)
                ? summaryElement.GetString()
                : null;

            var issues = new List<ExportVerificationIssue>();
            if (root.TryGetProperty("issues", out var issuesElement) && issuesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var issueElement in issuesElement.EnumerateArray())
                {
                    var severity = issueElement.TryGetProperty("severity", out var severityElement)
                        ? severityElement.GetString()
                        : null;
                    var title = issueElement.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString()
                        : null;
                    var description = issueElement.TryGetProperty("description", out var descriptionElement)
                        ? descriptionElement.GetString()
                        : null;
                    var recommendation = issueElement.TryGetProperty("recommendation", out var recommendationElement)
                        ? recommendationElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    issues.Add(new ExportVerificationIssue(
                        string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim(),
                        string.IsNullOrWhiteSpace(title) ? "Issue" : title.Trim(),
                        string.IsNullOrWhiteSpace(description) ? "No additional details provided." : description.Trim(),
                        string.IsNullOrWhiteSpace(recommendation) ? null : recommendation.Trim()));
                }
            }

            var fixes = ReadStringArray(root, "suggested_fixes");
            var fixArray = fixes.Count > 0 ? fixes.ToArray() : Array.Empty<string>();

            AssistantDirectiveBatch? directives = null;
            if (root.TryGetProperty("suggested_directives", out var directivesElement)
                && directivesElement.ValueKind == JsonValueKind.Object)
            {
                var directivePayload = ParseAssistantDirectivePayload(directivesElement.GetRawText());
                if (directivePayload is not null)
                {
                    directives = directivePayload;
                }
            }

            var summaryText = string.IsNullOrWhiteSpace(summary)
                ? (issues.Count > 0 ? "Issues detected in exports." : "Exports appear healthy.")
                : summary.Trim();

            var issueArray = issues.Count > 0 ? issues.ToArray() : Array.Empty<ExportVerificationIssue>();

            return new ExportVerificationResult(
                DateTimeOffset.UtcNow,
                summaryText,
                issueArray,
                fixArray,
                directives);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Overview, IReadOnlyList<LogTriageIssue> Issues)? ParseLogTriage(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var jsonText = ExtractJsonPayload(response);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            var overview = root.TryGetProperty("overallSummary", out var summaryElement)
                ? summaryElement.GetString()
                : null;

            var issues = new List<LogTriageIssue>();
            if (root.TryGetProperty("issues", out var issuesElement) && issuesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var issueElement in issuesElement.EnumerateArray())
                {
                    var category = issueElement.TryGetProperty("category", out var categoryElement)
                        ? categoryElement.GetString()
                        : null;
                    var severity = issueElement.TryGetProperty("severity", out var severityElement)
                        ? severityElement.GetString()
                        : null;
                    var description = issueElement.TryGetProperty("description", out var descriptionElement)
                        ? descriptionElement.GetString()
                        : null;
                    var recommendation = issueElement.TryGetProperty("recommendation", out var recommendationElement)
                        ? recommendationElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(recommendation))
                    {
                        continue;
                    }

                    issues.Add(new LogTriageIssue(
                        string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                        string.IsNullOrWhiteSpace(severity) ? "info" : severity.Trim(),
                        string.IsNullOrWhiteSpace(description) ? "No additional details provided." : description.Trim(),
                        string.IsNullOrWhiteSpace(recommendation) ? "No action recommended." : recommendation.Trim()));
                }
            }

            overview = string.IsNullOrWhiteSpace(overview)
                ? (issues.Count > 0 ? "Issues detected in recent logs." : "Logs appear healthy.")
                : overview.Trim();

            return (overview, issues);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MigrationScriptGenerationResult ParseAutomationScriptPayload(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return new MigrationScriptGenerationResult(
                null,
                Array.Empty<MigrationAutomationScript>(),
                Array.Empty<string>(),
                "Empty response from model.");
        }

        var jsonText = ExtractJsonPayload(response);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return new MigrationScriptGenerationResult(
                null,
                Array.Empty<MigrationAutomationScript>(),
                Array.Empty<string>(),
                "Assistant response did not contain JSON.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AutomationScriptPayload>(jsonText, s_scriptPayloadOptions);
            if (payload is null)
            {
                return new MigrationScriptGenerationResult(
                    null,
                    Array.Empty<MigrationAutomationScript>(),
                    Array.Empty<string>(),
                    "Assistant returned an empty payload.");
            }

            var scripts = payload.Scripts?.Select(script =>
            {
                if (string.IsNullOrWhiteSpace(script.Content))
                {
                    return null;
                }

                var language = string.IsNullOrWhiteSpace(script.Language)
                    ? "shell"
                    : script.Language.Trim();

                var notes = script.Notes?.Where(note => !string.IsNullOrWhiteSpace(note))
                    .Select(note => note.Trim())
                    .ToArray() ?? Array.Empty<string>();

                return new MigrationAutomationScript(
                    string.IsNullOrWhiteSpace(script.Name) ? "Automation script" : script.Name.Trim(),
                    string.IsNullOrWhiteSpace(script.Description) ? null : script.Description.Trim(),
                    language,
                    script.Content.Trim(),
                    string.IsNullOrWhiteSpace(script.FileName) ? null : script.FileName.Trim(),
                    notes);
            })
            .Where(item => item is not null)
            .Cast<MigrationAutomationScript>()
            .ToArray() ?? Array.Empty<MigrationAutomationScript>();

            var warnings = payload.Warnings?.Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => warning.Trim())
                .ToArray() ?? Array.Empty<string>();

            var summary = string.IsNullOrWhiteSpace(payload.Summary) ? null : payload.Summary.Trim();
            var error = string.IsNullOrWhiteSpace(payload.Error) ? null : payload.Error.Trim();

            return new MigrationScriptGenerationResult(summary, scripts, warnings, error);
        }
        catch (JsonException ex)
        {
            return new MigrationScriptGenerationResult(
                null,
                Array.Empty<MigrationAutomationScript>(),
                Array.Empty<string>(),
                $"Failed to parse automation script payload: {ex.Message}");
        }
    }

    private static AiArtifactAnnotation? ParseArtifactAnnotation(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var jsonText = ExtractJsonPayload(response);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            var summary = root.TryGetProperty("markdown_summary", out var summaryElement)
                ? summaryElement.GetString()
                : root.TryGetProperty("summary", out var fallbackSummary)
                    ? fallbackSummary.GetString()
                    : null;

            var recommendations = new List<AiRecommendation>();
            if (root.TryGetProperty("recommendations", out var recsElement) && recsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var recElement in recsElement.EnumerateArray())
                {
                    var title = recElement.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString()
                        : null;
                    var detail = recElement.TryGetProperty("summary", out var detailElement)
                        ? detailElement.GetString()
                        : null;
                    var prompts = ReadStringArray(recElement, "suggested_prompts");
                    if (prompts.Count == 0)
                    {
                        prompts = ReadStringArray(recElement, "prompts");
                    }

                    var assets = ReadStringArray(recElement, "related_assets");
                    if (assets.Count == 0)
                    {
                        assets = ReadStringArray(recElement, "assets");
                    }

                    var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Recommendation" : title.Trim();
                    var normalizedDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
                    var normalizedPrompts = prompts.Count > 0 ? prompts : new List<string>();
                    var normalizedAssets = assets.Count > 0 ? assets : null;

                    recommendations.Add(new AiRecommendation(
                        normalizedTitle,
                        normalizedDetail,
                        normalizedPrompts.Count > 0 ? normalizedPrompts : Array.Empty<string>(),
                        normalizedAssets));
                }
            }

            var materialRecommendations = recommendations.Count > 0
                ? recommendations.ToArray()
                : Array.Empty<AiRecommendation>();

            var normalizedSummary = string.IsNullOrWhiteSpace(summary) ? string.Empty : summary.Trim();

            if (string.IsNullOrWhiteSpace(normalizedSummary) && materialRecommendations.Length == 0)
            {
                return null;
            }

            return new AiArtifactAnnotation(DateTimeOffset.UtcNow, normalizedSummary, materialRecommendations);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ReadStringArray(JsonElement element, string propertyName)
    {
        var results = new List<string>();
        if (element.TryGetProperty(propertyName, out var arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arrayElement.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value.Trim());
                }
            }
        }

        return results;
    }

    private static string? ExtractJsonPayload(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newlineIndex = trimmed.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                trimmed = trimmed[(newlineIndex + 1)..];
            }

            var fenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceIndex >= 0)
            {
                trimmed = trimmed[..fenceIndex];
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace < firstBrace)
        {
            return null;
        }

        return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
    }

    public static ChatAssistantErrorGuidance CreateErrorGuidance(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        if (exception is AggregateException aggregate && aggregate.InnerException is not null)
        {
            return CreateErrorGuidance(aggregate.InnerException);
        }

        if (exception is RequestFailedException requestFailed)
        {
            return CreateRequestFailedGuidance(requestFailed);
        }

        if (exception is ArgumentException argumentException)
        {
            return CreateArgumentGuidance(argumentException);
        }

        if (exception is HttpRequestException)
        {
            return new ChatAssistantErrorGuidance(
                "The assistant endpoint could not be reached.",
                new[]
                {
                    "Verify the API endpoint URL is correct and reachable from this machine.",
                    "Confirm your network or VPN connection allows access to the Azure/OpenAI resource.",
                    "Try again once connectivity has been restored."
                });
        }

        if (exception.InnerException is not null)
        {
            return CreateErrorGuidance(exception.InnerException);
        }

        return new ChatAssistantErrorGuidance(
            "The assistant request failed unexpectedly.",
            new[]
            {
                "Review the application logs for detailed diagnostics.",
                "Retry the request. If it continues to fail, verify the assistant configuration."
            });
    }

    private static ChatAssistantErrorGuidance CreateArgumentGuidance(ArgumentException exception)
    {
        var message = exception.Message ?? string.Empty;

        if (message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatAssistantErrorGuidance(
                "An API key is required before the assistant can respond.",
                new[]
                {
                    "Open the assistant settings and paste a valid Azure/OpenAI API key.",
                    "Save the settings, then resend the prompt."
                });
        }

        if (message.Contains("endpoint", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatAssistantErrorGuidance(
                "The assistant endpoint URL is missing.",
                new[]
                {
                    "Provide the Azure/OpenAI endpoint URL in the assistant settings.",
                    "Ensure the URL is absolute, including the https:// prefix.",
                    "Retry the prompt after saving the updated endpoint."
                });
        }

        if (message.Contains("absolute URI", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatAssistantErrorGuidance(
                "The assistant endpoint must be an absolute URI.",
                new[]
                {
                    "Update the assistant endpoint to include the full https:// URL.",
                    "Confirm the hostname matches your Azure/OpenAI resource.",
                    "Retry the request once the endpoint is corrected."
                });
        }

        if (message.Contains("model", StringComparison.OrdinalIgnoreCase) || message.Contains("deployment", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatAssistantErrorGuidance(
                "A deployment or model name must be configured before chatting.",
                new[]
                {
                    "Enter the deployment or model identifier in the assistant settings.",
                    "Ensure the value matches an existing deployment on the configured endpoint.",
                    "Send the prompt again after saving the change."
                });
        }

        return new ChatAssistantErrorGuidance(
            "The assistant configuration is invalid.",
            new[]
            {
                "Review the assistant settings for missing or incorrect values.",
                "Correct the configuration and resend the request."
            });
    }

    private static ChatAssistantErrorGuidance CreateRequestFailedGuidance(RequestFailedException exception)
    {
        switch (exception.Status)
        {
            case 400:
                return new ChatAssistantErrorGuidance(
                    "The assistant rejected the request as invalid.",
                    new[]
                    {
                        "Shorten or simplify the prompt if it is very large.",
                        "Check the logs for the full error message from Azure/OpenAI.",
                        "Retry after adjusting the request parameters."
                    });

            case 401:
            case 403:
                return new ChatAssistantErrorGuidance(
                    "The assistant service rejected the API key.",
                    new[]
                    {
                        "Paste a valid Azure/OpenAI key in the assistant settings.",
                        "Confirm the key has access to the configured deployment.",
                        "Resend the prompt once the key has been updated."
                    });

            case 404:
                return new ChatAssistantErrorGuidance(
                    "The requested deployment was not found at the endpoint.",
                    new[]
                    {
                        "Verify the assistant endpoint URL matches your Azure/OpenAI resource.",
                        "Check that the deployment or model name is correct and published.",
                        "Retry the prompt after correcting the configuration."
                    });

            case 429:
                return new ChatAssistantErrorGuidance(
                    "The assistant is temporarily rate limited.",
                    new[]
                    {
                        "Wait a few seconds before retrying the request.",
                        "Reduce concurrent requests or review Azure/OpenAI quota settings if the issue persists."
                    });

            case 500:
            case 502:
            case 503:
            case 504:
                return new ChatAssistantErrorGuidance(
                    "The assistant service is currently unavailable.",
                    new[]
                    {
                        "Retry the prompt after a short delay.",
                        "Check the Azure/OpenAI service status if outages continue."
                    });
        }

        if (exception.Status == 0)
        {
            return new ChatAssistantErrorGuidance(
                "The assistant service did not return a response.",
                new[]
                {
                    "Verify the endpoint URL and network connectivity.",
                    "Inspect the logs for transport-level errors.",
                    "Retry once connectivity issues are resolved."
                });
        }

        var headline = exception.Status > 0
            ? $"The assistant request failed with status code {exception.Status}."
            : "The assistant request failed.";

        return new ChatAssistantErrorGuidance(
            headline,
            new[]
            {
                "Review the logs for the detailed Azure/OpenAI error payload.",
                "Retry after correcting any configuration or service issues."
            });
    }

    private sealed class AutomationScriptPayload
    {
        public string? Summary { get; set; }
        public List<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public List<AutomationScriptPayloadItem>? Scripts { get; set; }
    }

    private sealed class AutomationScriptPayloadItem
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? FileName { get; set; }
        public string? Content { get; set; }
        public List<string>? Notes { get; set; }
    }

    private static string FormatBoolean(bool value) => value ? "yes" : "no";

    private static string BuildDatasetSystemPrompt(
        string? systemPrompt,
        IReadOnlyList<AiIndexedDatasetReference> datasets,
        IReadOnlyList<ArtifactSearchResult> matches,
        Action<string>? diagnosticLogger)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            builder.AppendLine(systemPrompt.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("You are a data analyst answering questions about recently exported storefront datasets.");
        builder.AppendLine("Use the provided rows to calculate totals or list details. Mention file names when helpful and call out when data is missing.");
        builder.AppendLine();

        if (datasets.Count > 0)
        {
            builder.AppendLine("Indexed datasets:");
            foreach (var dataset in datasets)
            {
                builder.Append("- ");
                builder.Append(dataset.Name);
                if (dataset.SchemaHighlights.Count > 0)
                {
                    builder.Append(" (columns: ");
                    builder.Append(string.Join(", ", dataset.SchemaHighlights));
                    builder.Append(')');
                }

                builder.Append(" [index: ");
                builder.Append(dataset.VectorIndexIdentifier);
                builder.AppendLine("]");
            }

            builder.AppendLine();
        }

        builder.AppendLine("Reference rows:");
        var index = 1;
        foreach (var match in matches)
        {
            var sanitizedSnippet = PiIRedactor.Redact(match.Snippet);
            if (sanitizedSnippet.HasRedactions)
            {
                var summary = PiIRedactor.BuildSummary(sanitizedSnippet);
                var message = string.IsNullOrWhiteSpace(summary)
                    ? $"Redacted sensitive values for {match.DatasetName} row {match.RowNumber} before building the dataset prompt."
                    : $"Redacted {summary} for {match.DatasetName} row {match.RowNumber} before building the dataset prompt.";
                diagnosticLogger?.Invoke(message);
                Trace.WriteLine(message);
            }

            builder.Append('[');
            builder.Append(index++);
            builder.Append("] ");
            builder.Append(match.DatasetName);
            builder.Append(" (file: ");
            builder.Append(Path.GetFileName(match.FilePath));
            builder.Append(", row: ");
            builder.Append(match.RowNumber);
            builder.Append(", score: ");
            builder.Append(match.Score.ToString("0.000", CultureInfo.InvariantCulture));
            builder.AppendLine(")");
            builder.AppendLine(sanitizedSnippet.RedactedText);
            builder.AppendLine();
        }

        builder.AppendLine("Answer the operator using only the referenced data. If you need to estimate, explain the assumption.");

        return builder.ToString();
    }

    private static bool TryCreateRequestMessage(ChatMessage message, out ChatRequestMessage? requestMessage)
    {
        requestMessage = message.Role switch
        {
            ChatMessageRole.System => new ChatRequestSystemMessage(message.Content),
            ChatMessageRole.User => new ChatRequestUserMessage(message.Content),
            ChatMessageRole.Assistant => new ChatRequestAssistantMessage(message.Content),
            ChatMessageRole.Tool when !string.IsNullOrWhiteSpace(message.Content) => new ChatRequestAssistantMessage(message.Content),
            _ => null,
        };

        return requestMessage is not null;
    }

    internal static void ReportUsage(ChatSessionSettings settings, ChatUsageSnapshot? usage)
    {
        if (settings is null || usage is null)
        {
            return;
        }

        var cost = CalculateUsageCost(settings, usage);
        settings.ApplyUsageTotals(usage, cost);

        settings.UsageReported?.Invoke(usage);
    }

    private static decimal CalculateUsageCost(ChatSessionSettings settings, ChatUsageSnapshot usage)
    {
        decimal cost = 0m;

        if (settings.PromptTokenCostPerThousandUsd is { } promptRate && promptRate > 0m && usage.PromptTokens > 0)
        {
            cost += (usage.PromptTokens / 1000m) * promptRate;
        }

        if (settings.CompletionTokenCostPerThousandUsd is { } completionRate && completionRate > 0m && usage.CompletionTokens > 0)
        {
            cost += (usage.CompletionTokens / 1000m) * completionRate;
        }

        return cost;
    }

    private static bool HasRemainingBudget(ChatSessionSettings settings, out string? warningMessage)
    {
        if (settings.MaxPromptTokens is { } maxPrompt && settings.ConsumedPromptTokens >= maxPrompt)
        {
            warningMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Assistant session prompt-token budget reached (consumed {0:N0} of {1:N0}). Aborting request before contacting the API.",
                settings.ConsumedPromptTokens,
                maxPrompt);
            return false;
        }

        if (settings.MaxTotalTokens is { } maxTotal && settings.ConsumedTotalTokens >= maxTotal)
        {
            warningMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Assistant session total-token budget reached (consumed {0:N0} of {1:N0}). Aborting request before contacting the API.",
                settings.ConsumedTotalTokens,
                maxTotal);
            return false;
        }

        if (settings.MaxCostUsd is { } maxCost && settings.ConsumedCostUsd >= maxCost)
        {
            warningMessage = string.Format(
                CultureInfo.InvariantCulture,
                "Assistant session cost budget reached (spent ${0:F2} of ${1:F2}). Aborting request before contacting the API.",
                settings.ConsumedCostUsd,
                maxCost);
            return false;
        }

        warningMessage = null;
        return true;
    }

    private static ChatUsageSnapshot? CreateUsageSnapshot(CompletionsUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var promptTokens = usage.PromptTokens;
        var completionTokens = usage.CompletionTokens;
        var totalTokens = usage.TotalTokens;

        return new ChatUsageSnapshot(promptTokens, completionTokens, totalTokens);
    }

    private static ToolCallState GetOrCreateToolState(Dictionary<int, ToolCallState> states, int index)
    {
        if (!states.TryGetValue(index, out var state))
        {
            state = new ToolCallState(index);
            states[index] = state;
        }

        return state;
    }

    private sealed class ToolCallState
    {
        public ToolCallState(int index)
        {
            Index = index;
            Arguments = new StringBuilder();
        }

        public int Index { get; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; }
    }
}

public sealed record ChatSessionSettings(
    string Endpoint,
    string ApiKey,
    string Model,
    string? SystemPrompt,
    int? ToolCallIterationLimit = null,
    int? MaxPromptTokens = null,
    int? MaxTotalTokens = null,
    decimal? MaxCostUsd = null,
    decimal? PromptTokenCostPerThousandUsd = null,
    decimal? CompletionTokenCostPerThousandUsd = null,
    Action<string>? DiagnosticLogger = null,
    Action<ChatUsageSnapshot>? UsageReported = null)
{
    public long ConsumedPromptTokens { get; private set; }

    public long ConsumedCompletionTokens { get; private set; }

    public decimal ConsumedCostUsd { get; private set; }

    public long ConsumedTotalTokens => ConsumedPromptTokens + ConsumedCompletionTokens;

    internal void ApplyUsageTotals(ChatUsageSnapshot usage, decimal incrementalCostUsd)
    {
        if (usage is null)
        {
            return;
        }

        if (usage.PromptTokens > 0)
        {
            ConsumedPromptTokens += usage.PromptTokens;
        }

        if (usage.CompletionTokens > 0)
        {
            ConsumedCompletionTokens += usage.CompletionTokens;
        }

        if (incrementalCostUsd > 0)
        {
            ConsumedCostUsd += incrementalCostUsd;
        }
    }
}

public sealed record AssistantDirectiveBatch(
    string Summary,
    IReadOnlyList<AssistantToggleDirective> Toggles,
    AssistantRetryDirective? Retry,
    IReadOnlyList<AssistantActionDirective> Actions,
    IReadOnlyList<AssistantCredentialReminder> CredentialReminders,
    bool RequiresConfirmation,
    string? RiskNote);

public sealed record AssistantToggleDirective(
    string Name,
    bool Value,
    string? Justification,
    string? RiskLevel,
    double? Confidence,
    bool RequiresConfirmation);

public sealed record AssistantRetryDirective(
    bool? Enable,
    int? Attempts,
    double? BaseDelaySeconds,
    double? MaxDelaySeconds,
    string? Justification);

public sealed record AssistantActionDirective(
    string Name,
    string? Justification,
    bool RequiresConfirmation,
    RunPlan? Plan);

public sealed record AssistantCredentialReminder(string Credential, string Message);

public sealed record ChatSessionContext(
    PlatformMode SelectedPlatform,
    bool ExportCsv,
    bool ExportShopify,
    bool ExportWoo,
    bool ExportReviews,
    bool ExportXlsx,
    bool ExportJsonl,
    bool ExportPluginsCsv,
    bool ExportPluginsJsonl,
    bool ExportThemesCsv,
    bool ExportThemesJsonl,
    bool ExportPublicExtensionFootprints,
    bool ExportPublicDesignSnapshot,
    bool ExportPublicDesignScreenshots,
    bool ExportStoreConfiguration,
    bool ImportStoreConfiguration,
    bool HasWordPressCredentials,
    bool HasShopifyCredentials,
    bool HasTargetCredentials,
    bool EnableHttpRetries,
    int HttpRetryAttempts,
    string? AdditionalPublicExtensionPages,
    string? AdditionalDesignSnapshotPages,
    int IndexedDatasetCount,
    IReadOnlyList<string> IndexedDatasetNames);

public sealed record ChatAssistantErrorGuidance(string Headline, IReadOnlyList<string> Steps)
{
    public string ToMessage()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Headline);

        if (Steps is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("Next steps:");
            for (var i = 0; i < Steps.Count; i++)
            {
                builder.Append(i + 1).Append('.').Append(' ').AppendLine(Steps[i]);
            }
        }

        return builder.ToString().TrimEnd();
    }
}
