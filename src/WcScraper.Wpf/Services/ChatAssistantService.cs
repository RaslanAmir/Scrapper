using System;
using System.Collections.Generic;
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

        return builder.ToString();
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

    private static ChatRole ToChatRole(ChatMessageRole role)
        => role switch
        {
            ChatMessageRole.System => ChatRole.System,
            ChatMessageRole.Assistant => ChatRole.Assistant,
            ChatMessageRole.Tool => ChatRole.Tool,
            _ => ChatRole.User,
        };

    private static string FormatBoolean(bool value) => value ? "yes" : "no";
}

public sealed record ChatSessionSettings(string Endpoint, string ApiKey, string Model, string? SystemPrompt);

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
    string? AdditionalDesignSnapshotPages);
