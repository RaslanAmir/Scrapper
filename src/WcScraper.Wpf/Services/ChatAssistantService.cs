using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using WcScraper.Core;
using WcScraper.Wpf.Models;

namespace WcScraper.Wpf.Services;

public sealed class ChatAssistantService
{
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
