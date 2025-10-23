using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

namespace WcScraper.Wpf.Services;

public sealed class ChatAssistantService
{
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

        var options = new ChatCompletionsOptions
        {
            DeploymentName = settings.Model,
        };

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, promptBuilder.ToString()));

        foreach (var message in history)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ToChatRole(message.Role), message.Content));
        }

        var response = await client.GetChatCompletionsStreamingAsync(options, cancellationToken).ConfigureAwait(false);
        await using var streaming = response.Value;

        await foreach (var choice in streaming.GetChoicesStreaming(cancellationToken))
        {
            await foreach (var chatMessage in choice.GetMessageStreaming(cancellationToken))
            {
                foreach (var content in chatMessage.Content)
                {
                    if (content is ChatMessageTextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        yield return textContent.Text;
                    }
                }
            }
        }

        await streaming.CompleteAsync().ConfigureAwait(false);
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

        var systemPrompt = BuildDatasetSystemPrompt(settings.SystemPrompt, datasetInventory, matches);
        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, systemPrompt));

        foreach (var message in history)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ToChatRole(message.Role), message.Content));
        }

        var response = await client.GetChatCompletionsStreamingAsync(options, cancellationToken).ConfigureAwait(false);
        await using var streaming = response.Value;

        await foreach (var choice in streaming.GetChoicesStreaming(cancellationToken))
        {
            await foreach (var chatMessage in choice.GetMessageStreaming(cancellationToken))
            {
                foreach (var content in chatMessage.Content)
                {
                    if (content is ChatMessageTextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                    {
                        yield return textContent.Text;
                    }
                }
            }
        }

        await streaming.CompleteAsync().ConfigureAwait(false);
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
        systemPrompt.AppendLine("  \"requires_confirmation\": false,");
        systemPrompt.AppendLine("  \"risk_note\": \"summarize any risks\"");
        systemPrompt.AppendLine("}");
        systemPrompt.AppendLine("If there are no actionable changes, return empty arrays and false flags.");

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, systemPrompt.ToString()));

        var contextPrompt = BuildContextualPrompt(context);

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Latest operator message:");
        userPrompt.AppendLine(latestUserMessage.Trim());
        userPrompt.AppendLine();
        userPrompt.AppendLine("Current session context:");
        userPrompt.AppendLine(contextPrompt.Trim());
        userPrompt.AppendLine();
        userPrompt.AppendLine("Return JSON only. Do not include markdown fences.");

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.User, userPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, systemPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.User, userPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, systemPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.User, userPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, systemPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.User, userPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.System, systemPrompt.ToString()));

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

        options.Messages.Add(new Azure.AI.OpenAI.ChatMessage(ChatRole.User, userPrompt.ToString()));

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

            if (toggles.Count == 0 && retryDirective is null && reminders.Count == 0)
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
                reminders,
                requiresConfirmation,
                normalizedRiskNote);
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

    private static ChatRole ToChatRole(ChatMessageRole role)
        => role switch
        {
            ChatMessageRole.System => ChatRole.System,
            ChatMessageRole.Assistant => ChatRole.Assistant,
            ChatMessageRole.Tool => ChatRole.Tool,
            _ => ChatRole.User,
        };

    private static string FormatBoolean(bool value) => value ? "yes" : "no";

    private static string BuildDatasetSystemPrompt(
        string? systemPrompt,
        IReadOnlyList<AiIndexedDatasetReference> datasets,
        IReadOnlyList<ArtifactSearchResult> matches)
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
            builder.AppendLine(match.Snippet);
            builder.AppendLine();
        }

        builder.AppendLine("Answer the operator using only the referenced data. If you need to estimate, explain the assumption.");

        return builder.ToString();
    }
}

public sealed record ChatSessionSettings(string Endpoint, string ApiKey, string Model, string? SystemPrompt);

public sealed record AssistantDirectiveBatch(
    string Summary,
    IReadOnlyList<AssistantToggleDirective> Toggles,
    AssistantRetryDirective? Retry,
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
