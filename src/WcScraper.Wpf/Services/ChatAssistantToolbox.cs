using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;

namespace WcScraper.Wpf.Services;

public sealed class ChatAssistantToolbox
{
    private static readonly JsonSerializerOptions s_serializationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly Func<string?>? _latestRunSnapshotProvider;
    private readonly Func<int, IReadOnlyList<ExportFileSummary>>? _exportFileProvider;
    private readonly Func<int, IReadOnlyList<string>>? _recentLogsProvider;

    public ChatAssistantToolbox(
        Func<string?>? latestRunSnapshotProvider,
        Func<int, IReadOnlyList<ExportFileSummary>>? exportFileProvider,
        Func<int, IReadOnlyList<string>>? recentLogsProvider)
    {
        _latestRunSnapshotProvider = latestRunSnapshotProvider;
        _exportFileProvider = exportFileProvider;
        _recentLogsProvider = recentLogsProvider;

        ToolDefinitions = BuildToolDefinitions();
    }

    public IReadOnlyList<ChatCompletionsToolDefinition> ToolDefinitions { get; }

    public ValueTask<string> InvokeAsync(string? toolName, string? argumentsJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new InvalidOperationException("Tool name was not provided.");
        }

        var payload = toolName switch
        {
            ToolNames.GetLatestRunSnapshot => Serialize(GetLatestRunSnapshotPayload()),
            ToolNames.ListExportFiles => Serialize(ListExportFiles(ParseArguments<ListExportFilesArguments>(argumentsJson))),
            ToolNames.GetRecentLogs => Serialize(GetRecentLogs(ParseArguments<GetRecentLogsArguments>(argumentsJson))),
            _ => throw new InvalidOperationException($"Unsupported tool '{toolName}'.")
        };

        return ValueTask.FromResult(payload);
    }

    private LatestRunSnapshotPayload GetLatestRunSnapshotPayload()
    {
        if (_latestRunSnapshotProvider is null)
        {
            return new LatestRunSnapshotPayload(false, null, null);
        }

        var snapshot = _latestRunSnapshotProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return new LatestRunSnapshotPayload(false, null, null);
        }

        JsonNode? parsed = null;
        try
        {
            parsed = JsonNode.Parse(snapshot);
        }
        catch (JsonException)
        {
            // fall back to returning the raw string representation when parsing fails.
        }

        return new LatestRunSnapshotPayload(true, parsed, snapshot);
    }

    private ExportFileListPayload ListExportFiles(ListExportFilesArguments args)
    {
        var limit = Math.Clamp(args?.Limit ?? 10, 1, 50);
        if (_exportFileProvider is null)
        {
            return new ExportFileListPayload(Array.Empty<ExportFileSummary>());
        }

        IReadOnlyList<ExportFileSummary> files;
        try
        {
            files = _exportFileProvider(limit);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to enumerate export files: {ex.Message}", ex);
        }

        if (files.Count > limit)
        {
            files = files.Take(limit).ToArray();
        }

        return new ExportFileListPayload(files);
    }

    private RecentLogsPayload GetRecentLogs(GetRecentLogsArguments? args)
    {
        var limit = Math.Clamp(args?.Limit ?? 20, 1, 200);
        if (_recentLogsProvider is null)
        {
            return new RecentLogsPayload(Array.Empty<string>());
        }

        IReadOnlyList<string> logs;
        try
        {
            logs = _recentLogsProvider(limit);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read recent logs: {ex.Message}", ex);
        }

        if (logs.Count > limit)
        {
            logs = logs.Take(limit).ToArray();
        }

        return new RecentLogsPayload(logs);
    }

    private static T? ParseArguments<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, s_serializationOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The assistant provided invalid tool arguments.", ex);
        }
    }

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, s_serializationOptions);

    private static IReadOnlyList<ChatCompletionsToolDefinition> BuildToolDefinitions()
    {
        var tools = new List<ChatCompletionsToolDefinition>
        {
            new ChatCompletionsFunctionToolDefinition(new FunctionDefinition
            {
                Name = ToolNames.GetLatestRunSnapshot,
                Description = "Returns the most recent run snapshot, including store metadata, plugin summaries, and log highlights.",
                Parameters = BinaryData.FromString("{\n  \"type\": \"object\"\n}")
            }),
            new ChatCompletionsFunctionToolDefinition(new FunctionDefinition
            {
                Name = ToolNames.ListExportFiles,
                Description = "Lists the most recent export files generated by the scraper, ordered by recency.",
                Parameters = BinaryData.FromString("""
{
  "type": "object",
  "properties": {
    "limit": {
      "type": "integer",
      "minimum": 1,
      "maximum": 50,
      "description": "Optional maximum number of export files to include. Defaults to 10."
    }
  }
}
""")
            }),
            new ChatCompletionsFunctionToolDefinition(new FunctionDefinition
            {
                Name = ToolNames.GetRecentLogs,
                Description = "Retrieves the latest log lines from the current session to help troubleshoot scraping issues.",
                Parameters = BinaryData.FromString("""
{
  "type": "object",
  "properties": {
    "limit": {
      "type": "integer",
      "minimum": 1,
      "maximum": 200,
      "description": "Optional maximum number of log entries to include. Defaults to 20."
    }
  }
}
""")
            })
        };

        return tools;
    }

    private static class ToolNames
    {
        public const string GetLatestRunSnapshot = "get_latest_run_snapshot";
        public const string ListExportFiles = "list_export_files";
        public const string GetRecentLogs = "get_recent_logs";
    }

    public sealed record ExportFileSummary(string FileName, string FullPath, long? SizeBytes, DateTimeOffset? LastModifiedUtc);

    private sealed record LatestRunSnapshotPayload(bool HasSnapshot, JsonNode? Snapshot, string? Raw);

    private sealed record ExportFileListPayload(IReadOnlyList<ExportFileSummary> Files);

    private sealed record RecentLogsPayload(IReadOnlyList<string> Entries);

    private sealed record ListExportFilesArguments(int? Limit);

    private sealed record GetRecentLogsArguments(int? Limit);
}
